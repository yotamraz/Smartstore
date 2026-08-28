"""
Functional tests for TargetGroupEvaluatorTask modernization.

Validates:
1. Build compilation succeeds (dotnet build)
2. Full test suite passes without regressions (dotnet test)
3. New TargetGroupEvaluatorTask NUnit tests pass
4. TargetGroupEvaluatorTask implements ITask interface
5. DeleteSystemMappingsAsync extracted method exists in production code
"""

import os
import re
import subprocess

_THIS_FILE = os.path.abspath(__file__)
WORKSPACE_DIR = os.environ.get(
    "WORKSPACE_DIR",
    os.path.normpath(os.path.join(_THIS_FILE, "..", "..", "..", "..", "..")),
)
SMARTSTORE_DIR = os.path.join(WORKSPACE_DIR, "Smartstore")
TEST_PROJECT = os.path.join(
    SMARTSTORE_DIR,
    "test",
    "Smartstore.Core.Tests",
    "Smartstore.Core.Tests.csproj",
)
PRODUCTION_FILE = os.path.join(
    SMARTSTORE_DIR,
    "src",
    "Smartstore.Core",
    "Platform",
    "Identity",
    "Rules",
    "TargetGroupEvaluatorTask.cs",
)
TEST_FILE = os.path.join(
    SMARTSTORE_DIR,
    "test",
    "Smartstore.Core.Tests",
    "Platform",
    "Identity",
    "Rules",
    "TargetGroupEvaluatorTaskTests.cs",
)

EXPECTED_TEST_METHODS = [
    "Run_WithoutCustomerRoleIds_DeletesAllSystemMappings",
    "Run_WithCustomerRoleIds_DeletesOnlyMatchingSystemMappings",
    "Run_OnlyProcessesActiveRolesWithActiveRuleSets",
    "Run_EvaluatesEachActiveRuleSetViaRuleServiceAndTargetGroupService",
    "Run_CollectsCustomerIdsViaFastPagerAndAccumulatesPerRole",
    "Run_InsertsCustomerRoleMappingsInChunksOf500WithCommitAfterEach",
    "Run_DetachesCustomerRoleMappingEntitiesAfterProcessingRole",
    "Run_ClearsAclCacheWhenSystemMappingsDeleted",
    "Run_ClearsAclCacheWhenNewMappingsAdded",
    "Run_SkipsCacheInvalidationWhenNoMappingsChange",
    "Run_RespectsCancellationInRuleSetLoop",
    "Run_RespectsCancellationInChunkInsertionLoop",
]

# Dotnet path
SYSTEM_DOTNET = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "dotnet")
DOTNET_EXE = os.path.join(SYSTEM_DOTNET, "dotnet.exe")


def get_dotnet_env():
    """Build environment dict with correct dotnet path."""
    env = os.environ.copy()
    env["DOTNET_ROOT"] = SYSTEM_DOTNET
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_ROLL_FORWARD"] = "Major"
    env["PATH"] = SYSTEM_DOTNET + os.pathsep + env.get("PATH", "")
    return env


def run_dotnet(*args, timeout=600):
    """Run a dotnet command and return the result."""
    cmd = [DOTNET_EXE] + list(args)
    result = subprocess.run(
        cmd,
        cwd=SMARTSTORE_DIR,
        capture_output=True,
        text=True,
        timeout=timeout,
        env=get_dotnet_env(),
    )
    return result


class TestBuildCompilation:
    """Verify dotnet build succeeds for the test project and its dependencies."""

    def test_build_test_project(self):
        """Build the Smartstore.Core.Tests project in Release configuration."""
        result = run_dotnet(
            "build",
            TEST_PROJECT,
            "-c", "Release",
            timeout=600,
        )
        assert result.returncode == 0, (
            f"Build failed with exit code {result.returncode}.\n"
            f"STDOUT: {result.stdout[-2000:]}\n"
            f"STDERR: {result.stderr[-2000:]}"
        )
        assert "Build succeeded" in result.stdout


class TestFullTestSuite:
    """Verify the full NUnit test suite passes (no regressions)."""

    def test_all_tests_pass(self):
        """Run all tests in Smartstore.Core.Tests and verify no failures."""
        result = run_dotnet(
            "test",
            TEST_PROJECT,
            "-c", "Release",
            "--no-build",
            "--logger", "trx",
            timeout=600,
        )
        assert result.returncode == 0, (
            f"Tests failed with exit code {result.returncode}.\n"
            f"STDOUT: {result.stdout[-2000:]}\n"
            f"STDERR: {result.stderr[-2000:]}"
        )
        # Verify tests actually ran
        assert "Passed!" in result.stdout or "Test Run Successful" in result.stdout, (
            f"Could not confirm test success in output.\n"
            f"STDOUT: {result.stdout[-2000:]}"
        )

    def test_no_test_failures(self):
        """Run tests and verify zero failures in the output."""
        result = run_dotnet(
            "test",
            TEST_PROJECT,
            "-c", "Release",
            "--no-build",
            "-v", "normal",
            timeout=600,
        )
        assert result.returncode == 0, (
            f"Tests returned non-zero exit code.\n"
            f"STDOUT: {result.stdout[-2000:]}\n"
            f"STDERR: {result.stderr[-2000:]}"
        )
        # Check for zero failures in the summary
        assert "Test Run Successful" in result.stdout or "Passed!" in result.stdout, (
            f"Test failures detected.\n"
            f"STDOUT: {result.stdout[-2000:]}"
        )


class TestTargetGroupEvaluatorTests:
    """Verify the 12 new TargetGroupEvaluatorTask NUnit tests pass."""

    def test_targetgroup_tests_discoverable(self):
        """Verify the new tests are discoverable by dotnet test."""
        result = run_dotnet(
            "test",
            TEST_PROJECT,
            "-c", "Release",
            "--no-build",
            "--list-tests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"List tests failed.\n"
            f"STDOUT: {result.stdout[-2000:]}\n"
            f"STDERR: {result.stderr[-2000:]}"
        )
        # Check for specific test names
        for test_name in EXPECTED_TEST_METHODS:
            assert test_name in result.stdout, (
                f"Test '{test_name}' not found in discoverable tests.\n"
                f"STDOUT: {result.stdout[-2000:]}"
            )

    def test_targetgroup_tests_pass(self):
        """Run only TargetGroupEvaluatorTask tests and verify they pass."""
        result = run_dotnet(
            "test",
            TEST_PROJECT,
            "-c", "Release",
            "--no-build",
            "--filter", "FullyQualifiedName~TargetGroupEvaluatorTaskTests",
            "-v", "normal",
            timeout=300,
        )
        assert result.returncode == 0, (
            f"TargetGroupEvaluatorTask tests failed.\n"
            f"STDOUT: {result.stdout[-2000:]}\n"
            f"STDERR: {result.stderr[-2000:]}"
        )
        assert "Passed!" in result.stdout or "Test Run Successful" in result.stdout


class TestTypeRegistration:
    """Verify TargetGroupEvaluatorTask implements ITask and is discoverable."""

    def test_implements_itask(self):
        """Verify TargetGroupEvaluatorTask implements ITask interface in source."""
        with open(PRODUCTION_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        # Check that the class implements ITask
        assert "ITask" in content, "TargetGroupEvaluatorTask does not implement ITask"
        # Check the class declaration pattern (primary constructor syntax spans multiple lines)
        assert re.search(
            r"class\s+TargetGroupEvaluatorTask.*?:\s*ITask",
            content,
            re.DOTALL,
        ), "TargetGroupEvaluatorTask class declaration does not implement ITask"

    def test_run_method_signature(self):
        """Verify the Run method has the correct signature."""
        with open(PRODUCTION_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        assert re.search(
            r"public\s+async\s+Task\s+Run\s*\(\s*TaskExecutionContext\s+ctx",
            content,
        ), "Run method signature does not match expected pattern"

    def test_delete_system_mappings_method_exists(self):
        """Verify the extracted DeleteSystemMappingsAsync method exists."""
        with open(PRODUCTION_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        assert re.search(
            r"protected\s+virtual\s+Task<int>\s+DeleteSystemMappingsAsync",
            content,
        ), "DeleteSystemMappingsAsync method not found in production code"

    def test_scheduler_module_registers_task(self):
        """Verify TargetGroupEvaluatorTask is used in SchedulerModule or InvariantSeedData."""
        found = False
        # Check InvariantSeedData files
        search_dirs = [
            os.path.join(SMARTSTORE_DIR, "src", "Smartstore.Core", "Data", "Bootstrapping"),
            os.path.join(SMARTSTORE_DIR, "src", "Smartstore.Core", "Platform", "Scheduling"),
        ]
        for search_dir in search_dirs:
            if os.path.isdir(search_dir):
                for root, dirs, files in os.walk(search_dir):
                    for fname in files:
                        if fname.endswith(".cs"):
                            fpath = os.path.join(root, fname)
                            with open(fpath, "r", encoding="utf-8") as f:
                                content = f.read()
                            if "TargetGroupEvaluatorTask" in content:
                                found = True
                                break
                    if found:
                        break
            if found:
                break
        assert found, (
            "TargetGroupEvaluatorTask not referenced in SchedulerModule or InvariantSeedData"
        )


class TestTestFileIntegrity:
    """Verify the test file structure and completeness."""

    def test_test_file_exists(self):
        """Verify the test file exists at the expected location."""
        assert os.path.isfile(TEST_FILE), (
            f"Test file not found at {TEST_FILE}"
        )

    def test_test_file_has_all_test_methods(self):
        """Verify the test file contains all 12 expected test methods."""
        with open(TEST_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        for test_name in EXPECTED_TEST_METHODS:
            assert test_name in content, (
                f"Test method '{test_name}' not found in test file"
            )

    def test_test_file_uses_nunit(self):
        """Verify the test file uses NUnit framework."""
        with open(TEST_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        assert "using NUnit.Framework" in content
        assert "[TestFixture]" in content
        assert "[Test]" in content
        assert "[SetUp]" in content

    def test_test_file_uses_moq(self):
        """Verify the test file uses Moq for mocking."""
        with open(TEST_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        assert "using Moq" in content
        assert "Mock<" in content

    def test_testable_subclass_overrides_delete(self):
        """Verify TestableTargetGroupEvaluatorTask overrides DeleteSystemMappingsAsync."""
        with open(TEST_FILE, "r", encoding="utf-8") as f:
            content = f.read()
        assert "TestableTargetGroupEvaluatorTask" in content
        assert "DeleteSystemMappingsAsync" in content
        assert "TargetGroupEvaluatorTask" in content
