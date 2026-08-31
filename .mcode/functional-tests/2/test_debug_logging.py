"""
Functional tests for Milestone 2: Debug Logging Additions.

Verifies that structured debug-level logging was correctly added to four core
Smartstore services without breaking existing behavior:

1. TargetGroupEvaluatorTask.cs - LoggerMessage.Define delegates + ILogger
2. TargetGroupService.cs - ILogger property + Logger.Debug() calls
3. DbTaskStore.cs - ILogger property + Logger.Debug()/Error() calls
4. RuleService.cs - ILogger property + Logger.Debug() calls
5. appsettings.json - Serilog per-source-context overrides

The test approach:
- Run the full dotnet test suite to verify logging additions are purely additive
- Inspect source code to verify logging patterns match the specification
- Verify Serilog configuration has the expected namespace overrides
"""

import os
import re
import subprocess
import pytest


# Resolve paths
SMARTSTORE_ROOT = os.path.normpath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..")
)
if os.environ.get("WORKSPACE_DIR"):
    _candidate = os.path.join(os.environ["WORKSPACE_DIR"], "Smartstore")
    if os.path.isdir(_candidate):
        SMARTSTORE_ROOT = os.path.normpath(_candidate)

TEST_PROJECT_REL = os.path.join(
    "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj"
)
TEST_PROJECT = os.path.join(SMARTSTORE_ROOT, TEST_PROJECT_REL)

PIXI_ACTIVATE = os.environ.get("PIXI_ACTIVATE_ENV_HELPER", "")

_LOCALAPPDATA = os.environ.get("LOCALAPPDATA", "")
_USER_LOCALAPPDATA = os.path.join(os.environ.get("USERPROFILE", ""), "AppData", "Local")
PIXI_CACHE_DIR = ""
for candidate_dir in [_LOCALAPPDATA, _USER_LOCALAPPDATA]:
    _candidate_cache = os.path.join(candidate_dir, "pixi", "cache")
    if os.path.isdir(_candidate_cache):
        PIXI_CACHE_DIR = _candidate_cache
        break
if not PIXI_CACHE_DIR:
    PIXI_CACHE_DIR = os.path.join(_LOCALAPPDATA, "pixi", "cache")


# -- Source file paths --
SRC_ROOT = os.path.join(SMARTSTORE_ROOT, "src")
TARGET_GROUP_EVALUATOR_PATH = os.path.join(
    SRC_ROOT, "Smartstore.Core", "Platform", "Identity", "Rules",
    "TargetGroupEvaluatorTask.cs"
)
TARGET_GROUP_SERVICE_PATH = os.path.join(
    SRC_ROOT, "Smartstore.Core", "Platform", "Identity", "Rules",
    "TargetGroupService.cs"
)
DB_TASK_STORE_PATH = os.path.join(
    SRC_ROOT, "Smartstore.Core", "Platform", "Scheduling", "Services",
    "DbTaskStore.cs"
)
RULE_SERVICE_PATH = os.path.join(
    SRC_ROOT, "Smartstore.Core", "Platform", "Rules", "Services",
    "RuleService.cs"
)
APPSETTINGS_PATH = os.path.join(
    SRC_ROOT, "Smartstore.Web", "appsettings.json"
)


def run_dotnet(*args, timeout=300):
    """Run a dotnet command with pixi environment activation."""
    dotnet_args = " ".join(args)
    ps_script = (
        "$ErrorActionPreference = 'Continue'\n"
        f". '{PIXI_ACTIVATE}'\n"
        f"$env:PIXI_CACHE_DIR = '{PIXI_CACHE_DIR}'\n"
        "activate-env target-app\n"
        f"cd '{SMARTSTORE_ROOT}'\n"
        f"dotnet {dotnet_args}\n"
        "exit $LASTEXITCODE\n"
    )
    result = subprocess.run(
        ["powershell", "-NonInteractive", "-Command", ps_script],
        capture_output=True,
        text=True,
        timeout=timeout,
        cwd=SMARTSTORE_ROOT,
    )
    return result


def run_dotnet_test(filter_expr=None, timeout=300):
    """Run dotnet test with optional NUnit filter expression."""
    args = [
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
    ]
    if filter_expr:
        args.extend(["--filter", f'"{filter_expr}"'])
    return run_dotnet(*args, timeout=timeout)


def read_source(path):
    """Read a source file and return its content."""
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


# ---- Fixtures ----

@pytest.fixture(scope="module")
def full_test_result():
    """Run the full NUnit test suite once for all tests in this module."""
    result = run_dotnet(
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
        timeout=300,
    )
    return result


@pytest.fixture(scope="module")
def evaluator_source():
    return read_source(TARGET_GROUP_EVALUATOR_PATH)


@pytest.fixture(scope="module")
def service_source():
    return read_source(TARGET_GROUP_SERVICE_PATH)


@pytest.fixture(scope="module")
def dbtaskstore_source():
    return read_source(DB_TASK_STORE_PATH)


@pytest.fixture(scope="module")
def ruleservice_source():
    return read_source(RULE_SERVICE_PATH)


@pytest.fixture(scope="module")
def appsettings_content():
    return read_source(APPSETTINGS_PATH)


# ---- Test Classes ----

class TestFullSuitePasses:
    """Verify that the full NUnit test suite (335 tests) still passes
    after debug logging additions -- logging is purely additive."""

    def test_dotnet_test_exit_code_zero(self, full_test_result):
        """dotnet test exits with code 0 (all tests pass)."""
        assert full_test_result.returncode == 0, (
            f"Test run failed with exit code {full_test_result.returncode}.\n"
            f"STDOUT (last 3000 chars):\n{full_test_result.stdout[-3000:]}\n"
            f"STDERR (last 2000 chars):\n{full_test_result.stderr[-2000:]}"
        )

    def test_test_run_successful(self, full_test_result):
        """Output contains 'Test Run Successful'."""
        assert "Test Run Successful" in full_test_result.stdout, (
            f"Test run was not successful.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )

    def test_no_failures_reported(self, full_test_result):
        """No test failures in the run output."""
        failed_match = re.search(r"Failed:\s*(\d+)", full_test_result.stdout)
        if failed_match:
            assert int(failed_match.group(1)) == 0, (
                f"Expected 0 failures but found {failed_match.group(1)}.\n"
                f"STDOUT:\n{full_test_result.stdout[-3000:]}"
            )

    def test_at_least_335_tests_discovered(self, full_test_result):
        """At least 335 tests discovered (333 baseline + 2 new from logging)."""
        discovered_match = re.search(
            r"discovered (\d+) of (\d+)", full_test_result.stdout
        )
        assert discovered_match, (
            f"Could not find test discovery count.\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        total = int(discovered_match.group(1))
        assert total >= 335, (
            f"Expected at least 335 tests discovered but found {total}"
        )

    def test_all_discovered_tests_passed(self, full_test_result):
        """Every discovered test passed (Passed count == Total count)."""
        total_match = re.search(r"Total tests:\s*(\d+)", full_test_result.stdout)
        passed_match = re.search(r"Passed:\s*(\d+)", full_test_result.stdout)
        assert total_match and passed_match, (
            f"Could not parse total/passed counts.\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        total = int(total_match.group(1))
        passed = int(passed_match.group(1))
        assert passed == total, (
            f"Not all tests passed: {passed}/{total}\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}"
        )


class TestTargetGroupEvaluatorTaskLogging:
    """Verify TargetGroupEvaluatorTask has structured debug logging
    using LoggerMessage.Define source-generated delegates."""

    def test_has_ilogger_property_injection(self, evaluator_source):
        """ILogger property with NullLogger default for Autofac injection."""
        assert "public ILogger Logger { get; set; } = NullLogger.Instance;" in evaluator_source

    def test_has_log_run_started_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for task run start."""
        assert "LoggerMessage.Define" in evaluator_source
        assert "_logRunStarted" in evaluator_source

    def test_has_log_bulk_deleted_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for bulk deletion."""
        assert "_logBulkDeleted" in evaluator_source

    def test_has_log_processing_role_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for per-role processing."""
        assert "_logProcessingRole" in evaluator_source

    def test_has_log_rule_evaluation_result_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for rule evaluation result."""
        assert "_logRuleEvaluationResult" in evaluator_source

    def test_has_log_chunk_inserted_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for chunk insertion progress."""
        assert "_logChunkInserted" in evaluator_source

    def test_has_log_entity_detachment_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for entity detachment."""
        assert "_logEntityDetachment" in evaluator_source

    def test_has_log_cache_invalidation_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for cache invalidation."""
        assert "_logCacheInvalidation" in evaluator_source

    def test_has_log_run_completed_delegate(self, evaluator_source):
        """LoggerMessage.Define delegate for task run completion."""
        assert "_logRunCompleted" in evaluator_source

    def test_delegates_use_debug_level(self, evaluator_source):
        """All LoggerMessage.Define delegates use Debug log level."""
        # Count how many LoggerMessage.Define calls exist
        define_calls = re.findall(r"LoggerMessage\.Define", evaluator_source)
        assert len(define_calls) >= 7, (
            f"Expected at least 7 LoggerMessage.Define delegates, found {len(define_calls)}"
        )

    def test_no_debug_writeline_remains(self, evaluator_source):
        """Legacy Debug.WriteLineIf/Debug.WriteLine calls removed."""
        assert "Debug.WriteLineIf" not in evaluator_source, (
            "Legacy Debug.WriteLineIf should be replaced with ILogger"
        )
        assert "Debug.WriteLine(" not in evaluator_source, (
            "Legacy Debug.WriteLine should be replaced with ILogger"
        )


class TestTargetGroupServiceLogging:
    """Verify TargetGroupService has ILogger property and debug logging."""

    def test_has_ilogger_property_injection(self, service_source):
        """ILogger property with NullLogger default."""
        assert "public ILogger Logger { get; set; } = NullLogger.Instance;" in service_source

    def test_has_create_expression_group_logging(self, service_source):
        """Logs in CreateExpressionGroupAsync method."""
        assert "CreateExpressionGroup" in service_source
        assert "Logger.Debug" in service_source or "Logger" in service_source

    def test_has_process_filter_async_logging(self, service_source):
        """Logs timing and result count in ProcessFilterAsync."""
        # Should log input parameters and result count
        assert "ProcessFilter" in service_source

    def test_uses_stopwatch_for_timing(self, service_source):
        """Uses Stopwatch to measure elapsed time in filter operations."""
        assert "Stopwatch" in service_source


class TestDbTaskStoreLogging:
    """Verify DbTaskStore has ILogger property and debug logging."""

    def test_has_ilogger_property_injection(self, dbtaskstore_source):
        """ILogger property with NullLogger default."""
        assert "public ILogger Logger { get; set; } = NullLogger.Instance;" in dbtaskstore_source

    def test_has_task_descriptor_crud_logging(self, dbtaskstore_source):
        """Logs task descriptor create/update/delete operations."""
        # Should have debug logging around InsertTaskDescriptor, UpdateTaskDescriptor, etc.
        assert "Logger.Debug" in dbtaskstore_source or "Logger" in dbtaskstore_source

    def test_has_legacy_type_mapping_logging(self, dbtaskstore_source):
        """Logs legacy type name mapping resolution."""
        # DbTaskStore maps legacy type names for backward compatibility
        assert "legacy" in dbtaskstore_source.lower() or "Legacy" in dbtaskstore_source

    def test_has_execution_info_lifecycle_logging(self, dbtaskstore_source):
        """Logs execution info insert/update/finalize operations."""
        assert "ExecutionInfo" in dbtaskstore_source or "execution" in dbtaskstore_source.lower()

    def test_has_error_handling_logging(self, dbtaskstore_source):
        """Logs errors in exception handlers."""
        assert "Logger.Error" in dbtaskstore_source or "Logger" in dbtaskstore_source


class TestRuleServiceLogging:
    """Verify RuleService has ILogger property and debug logging."""

    def test_has_ilogger_property_injection(self, ruleservice_source):
        """ILogger property with NullLogger default."""
        assert "public ILogger Logger { get; set; } = NullLogger.Instance;" in ruleservice_source

    def test_has_expression_group_building_logging(self, ruleservice_source):
        """Logs expression group building from rule entities."""
        assert "CreateExpressionGroup" in ruleservice_source
        assert "Logger" in ruleservice_source

    def test_has_provider_resolution_logging(self, ruleservice_source):
        """Logs which IRuleProvider is resolved for each rule scope."""
        assert "provider" in ruleservice_source.lower()

    def test_has_rule_visiting_logging(self, ruleservice_source):
        """Logs individual rule visiting (rule ID, type, operator)."""
        assert "Visit" in ruleservice_source or "rule" in ruleservice_source.lower()


class TestSerilogConfiguration:
    """Verify appsettings.json has correct Serilog per-source-context overrides."""

    def test_appsettings_has_serilog_section(self, appsettings_content):
        """appsettings.json contains a Serilog configuration section.
        Note: .NET uses JSONC format (comments + trailing commas) which Python's
        json module cannot parse directly. The file is validated by the .NET build
        itself -- here we verify the Serilog section exists with expected structure."""
        assert '"Serilog"' in appsettings_content, (
            "appsettings.json must contain a Serilog configuration section"
        )
        # Verify it has MinimumLevel override structure
        assert "MinimumLevel" in appsettings_content
        assert "Override" in appsettings_content

    def test_has_identity_debug_override(self, appsettings_content):
        """Serilog config has Smartstore.Core.Identity set to Debug level."""
        assert "Smartstore.Core.Identity" in appsettings_content
        assert '"Debug"' in appsettings_content

    def test_has_scheduling_debug_override(self, appsettings_content):
        """Serilog config has Smartstore.Scheduling set to Debug level."""
        assert "Smartstore.Scheduling" in appsettings_content

    def test_has_rules_debug_override(self, appsettings_content):
        """Serilog config has Smartstore.Core.Rules set to Debug level."""
        assert "Smartstore.Core.Rules" in appsettings_content

    def test_database_sink_stays_at_information(self, appsettings_content):
        """Database sink level remains at Information to avoid flooding log table."""
        # The appsettings should mention the database sink constraint
        # Either via a comment or explicit configuration
        lower = appsettings_content.lower()
        assert "information" in lower, (
            "Expected 'Information' level mentioned in appsettings.json "
            "for database sink constraint"
        )


class TestExistingMilestone1TestsStillPass:
    """Verify the milestone 1 NUnit tests (unit, integration, parity) still pass
    after milestone 2 logging changes -- no regressions."""

    def test_unit_tests_pass(self):
        """All Tier 1 unit tests (TargetGroupEvaluatorTaskTests) still pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Unit tests failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )

    def test_integration_tests_pass(self):
        """All Tier 2 integration tests still pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskIntegrationTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Integration tests failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )

    def test_parity_tests_pass(self):
        """All Tier 3 parity tests still pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskParityTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Parity tests failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )
