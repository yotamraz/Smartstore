"""
Functional tests for the TargetGroupEvaluatorTask modernization milestone.

These tests verify:
1. The test project builds successfully
2. All 333 NUnit tests pass (including the 25 new TargetGroupEvaluatorTask tests)
3. The parity fix (CustomerRoleIds parameter filtering) is correct
4. Task registration is intact (CRON schedule, StopOnError)
5. Legacy type name mapping exists in DbTaskStore
6. No regressions in existing tests
"""

import os
import re
import subprocess
import pytest


# Resolve paths -- the Smartstore repo root
# This file is at Smartstore/.mcode/functional-tests/1/test_*.py
# Go up 3 dirs from test file dir: 1/ -> functional-tests/ -> .mcode/ -> Smartstore/
SMARTSTORE_ROOT = os.path.normpath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..")
)
# Fallback: if WORKSPACE_DIR is set, use it
if os.environ.get("WORKSPACE_DIR"):
    _candidate = os.path.join(os.environ["WORKSPACE_DIR"], "Smartstore")
    if os.path.isdir(_candidate):
        SMARTSTORE_ROOT = os.path.normpath(_candidate)

TEST_PROJECT_REL = os.path.join(
    "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj"
)
TEST_PROJECT = os.path.join(SMARTSTORE_ROOT, TEST_PROJECT_REL)

# The pixi activation script
PIXI_ACTIVATE = os.environ.get("PIXI_ACTIVATE_ENV_HELPER", "")

# Discover the correct LOCALAPPDATA for pixi cache
# On this sandbox, LOCALAPPDATA may point to system profile; check user profile too
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


def run_dotnet(*args, timeout=300):
    """
    Run a dotnet command with pixi environment activation.
    Returns the CompletedProcess result.
    """
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
    """
    Run dotnet test with optional NUnit filter expression.
    Returns CompletedProcess.
    """
    args = [
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
    ]
    if filter_expr:
        args.extend(["--filter", f'"{filter_expr}"'])

    result = run_dotnet(*args, timeout=timeout)
    return result


# ---- Fixtures ----

@pytest.fixture(scope="module")
def build_result():
    """Build the test project once for all tests in this module."""
    result = run_dotnet("build", TEST_PROJECT_REL, "--no-restore")
    return result


@pytest.fixture(scope="module")
def full_test_result():
    """Run the full test suite once for all tests in this module."""
    result = run_dotnet(
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
        timeout=300,
    )
    return result


# ---- Test Classes ----

class TestBuildSucceeds:
    """Verify the test project builds successfully."""

    def test_build_exit_code(self, build_result):
        """dotnet build exits with code 0."""
        assert build_result.returncode == 0, (
            f"Build failed with exit code {build_result.returncode}.\n"
            f"STDOUT:\n{build_result.stdout[-2000:]}\n"
            f"STDERR:\n{build_result.stderr[-2000:]}"
        )

    def test_build_produces_dll(self, build_result):
        """Build produces the test assembly DLL."""
        dll_path = os.path.join(
            SMARTSTORE_ROOT, "test", "Smartstore.Core.Tests",
            "bin", "Debug", "Smartstore.Core.Tests.dll"
        )
        assert os.path.exists(dll_path), f"Expected DLL not found at {dll_path}"

    def test_build_no_errors(self, build_result):
        """Build output contains 0 Error(s)."""
        assert "0 Error(s)" in build_result.stdout or build_result.returncode == 0, (
            f"Build reported errors.\nSTDOUT:\n{build_result.stdout[-2000:]}"
        )


class TestAllTestsPass:
    """Verify all 333 NUnit tests pass."""

    def test_full_suite_exit_code(self, full_test_result):
        """dotnet test exits with code 0."""
        assert full_test_result.returncode == 0, (
            f"Test run failed with exit code {full_test_result.returncode}.\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}\n"
            f"STDERR:\n{full_test_result.stderr[-2000:]}"
        )

    def test_discovers_333_tests(self, full_test_result):
        """NUnit discovers all 333 test cases."""
        assert "discovered 333 of 333 NUnit test cases" in full_test_result.stdout, (
            f"Expected 333 tests discovered.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )

    def test_all_333_pass(self, full_test_result):
        """Test run reports 333 passed, 0 failed."""
        assert "Test Run Successful" in full_test_result.stdout, (
            f"Test run was not successful.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        # Check for "Passed: 333" with flexible whitespace
        assert re.search(r"Passed:\s*333", full_test_result.stdout), (
            f"Expected 333 passed tests.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )

    def test_no_failures(self, full_test_result):
        """No test failures in the run."""
        failed_match = re.search(r"Failed:\s*(\d+)", full_test_result.stdout)
        if failed_match:
            assert int(failed_match.group(1)) == 0, (
                f"Expected 0 failures but found {failed_match.group(1)}.\n"
                f"STDOUT:\n{full_test_result.stdout[-3000:]}"
            )


class TestParityFix:
    """Verify the parity fix for CustomerRoleIds parameter filtering."""

    def test_customerroleids_parameter_scopes_test_passes(self):
        """The CustomerRoleIds parameter scoping test passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~CustomerRoleIds_parameter_scopes_deletion_and_role_processing",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Parity fix test failed with exit code {result.returncode}.\n"
            f"STDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )

    def test_source_code_has_correct_filter(self):
        """TargetGroupEvaluatorTask.cs properly filters roles by CustomerRoleIds."""
        task_path = os.path.join(
            SMARTSTORE_ROOT, "src", "Smartstore.Core", "Platform",
            "Identity", "Rules", "TargetGroupEvaluatorTask.cs"
        )
        with open(task_path, "r", encoding="utf-8") as f:
            source = f.read()

        # Verify the parameter check exists
        assert 'ctx.Parameters.ContainsKey("CustomerRoleIds")' in source, (
            "TargetGroupEvaluatorTask must check for CustomerRoleIds parameter"
        )

        # Verify role filtering is applied when parameter is present
        assert "roleIds.Contains(x.Id)" in source, (
            "TargetGroupEvaluatorTask must filter roles by roleIds when CustomerRoleIds is provided"
        )

        # Verify delete query is also scoped by roleIds
        assert "roleIds.Contains(x.CustomerRoleId)" in source, (
            "TargetGroupEvaluatorTask must scope deletion by CustomerRoleId when filter is active"
        )


class TestTaskRegistration:
    """Verify task registration (CRON schedule, StopOnError)."""

    def test_cron_schedule_test_passes(self):
        """The CRON schedule parity test passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Default_cron_schedule_is_0215_daily",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"CRON schedule test failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )

    def test_stop_on_error_test_passes(self):
        """The StopOnError = false parity test passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~StopOnError_is_false",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"StopOnError test failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )

    def test_seed_data_contains_task_registration(self):
        """InvariantSeedData.cs contains TargetGroupEvaluatorTask registration."""
        seed_path = os.path.join(
            SMARTSTORE_ROOT, "src", "Smartstore.Core", "Platform",
            "Installation", "SeedData", "InvariantSeedData.cs"
        )
        with open(seed_path, "r", encoding="utf-8") as f:
            source = f.read()

        assert "nameof(TargetGroupEvaluatorTask)" in source, (
            "InvariantSeedData must reference TargetGroupEvaluatorTask by name"
        )
        assert 'CronExpression = "15 2 * * *"' in source, (
            "TargetGroupEvaluatorTask must be scheduled at 02:15 daily"
        )


class TestLegacyTypeMapping:
    """Verify DbTaskStore legacy type name mapping."""

    def test_legacy_mapping_test_passes(self):
        """The legacy type name mapping test passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Legacy_type_name_mapping_in_DbTaskStore",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Legacy mapping test failed.\nSTDOUT:\n{result.stdout[-2000:]}\n"
            f"STDERR:\n{result.stderr[-1000:]}"
        )


class TestNoRegressions:
    """Verify no regressions in the existing test suite."""

    def test_existing_tests_count(self, full_test_result):
        """At least 333 tests pass (308 pre-existing + 25 new)."""
        total_match = re.search(r"Total tests:\s*(\d+)", full_test_result.stdout)
        assert total_match, (
            f"Could not find total test count.\nSTDOUT:\n{full_test_result.stdout[-2000:]}"
        )
        total = int(total_match.group(1))
        assert total >= 333, (
            f"Expected at least 333 tests but found {total}"
        )

    def test_unit_tests_pass(self):
        """All Tier 1 unit tests (10 tests) pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Unit tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )
        # Verify the expected 10 unit tests ran
        discovered = re.search(r"discovered (\d+) of (\d+)", result.stdout)
        if discovered:
            assert int(discovered.group(1)) == 10, (
                f"Expected 10 unit tests, found {discovered.group(1)}"
            )

    def test_integration_tests_pass(self):
        """All Tier 2 integration tests (8 tests) pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskIntegrationTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Integration tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )

    def test_parity_tests_pass(self):
        """All Tier 3 parity tests (7 tests) pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskParityTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Parity tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )
