"""
Functional tests for Milestone 3: Reduce Third-Party Dependencies and Optimize Performance.

These tests verify:
1. Build succeeds after dependency removal (BouncyCastle, JsMin)
2. All 335 NUnit tests pass with no regressions
3. BouncyCastle has been removed from Smartstore.Core.csproj
4. PreMailer.Net dead reference removed from Smartstore.csproj (kept in Smartstore.Core where used)
5. JsMin replaced with NUglify across all usage sites
6. JsMinProcessor.cs removed
7. TargetGroupEvaluatorTask uses AddRange for bulk inserts
8. NUglify referenced in correct projects
"""

import os
import re
import sys
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from shared_dotnet_helpers import (  # noqa: E402
    SMARTSTORE_ROOT,
    TEST_PROJECT_REL,
    run_dotnet,
    run_dotnet_test,
)


def read_file(relative_path):
    """Read a file relative to the Smartstore root."""
    full_path = os.path.join(SMARTSTORE_ROOT, relative_path)
    with open(full_path, "r", encoding="utf-8") as f:
        return f.read()


# ---- Fixtures ----

@pytest.fixture(scope="module")
def build_result():
    """Build the test project once for all tests in this module."""
    return run_dotnet("build", TEST_PROJECT_REL, "--no-restore")


@pytest.fixture(scope="module")
def full_test_result():
    """Run the full test suite once for all tests in this module."""
    return run_dotnet(
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
        timeout=300,
    )


# ---- Test Classes ----

class TestBuildAfterDependencyChanges:
    """Verify the project builds successfully after dependency removal."""

    def test_build_exit_code(self, build_result):
        """dotnet build exits with code 0 after removing BouncyCastle and JsMin."""
        assert build_result.returncode == 0, (
            f"Build failed with exit code {build_result.returncode}.\n"
            f"STDOUT:\n{build_result.stdout[-2000:]}\n"
            f"STDERR:\n{build_result.stderr[-2000:]}"
        )

    def test_build_no_errors(self, build_result):
        """Build output contains 0 Error(s)."""
        assert "0 Error(s)" in build_result.stdout or build_result.returncode == 0, (
            f"Build reported errors.\nSTDOUT:\n{build_result.stdout[-2000:]}"
        )

    def test_build_produces_dll(self, build_result):
        """Build produces the test assembly DLL."""
        dll_path = os.path.join(
            SMARTSTORE_ROOT, "test", "Smartstore.Core.Tests",
            "bin", "Debug", "Smartstore.Core.Tests.dll"
        )
        assert os.path.exists(dll_path), f"Expected DLL not found at {dll_path}"


class TestAllTestsPass:
    """Verify all NUnit tests pass after milestone 3 changes."""

    def test_full_suite_exit_code(self, full_test_result):
        """dotnet test exits with code 0."""
        assert full_test_result.returncode == 0, (
            f"Test run failed with exit code {full_test_result.returncode}.\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}\n"
            f"STDERR:\n{full_test_result.stderr[-2000:]}"
        )

    def test_all_335_pass(self, full_test_result):
        """Test run reports 335 passed, 0 failed."""
        assert "Test Run Successful" in full_test_result.stdout, (
            f"Test run was not successful.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        passed_match = re.search(r"Passed:\s*(\d+)", full_test_result.stdout)
        assert passed_match, (
            f"Could not find passed count.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        passed = int(passed_match.group(1))
        assert passed >= 335, (
            f"Expected at least 335 passed tests but found {passed}.\n"
            f"STDOUT:\n{full_test_result.stdout[-3000:]}"
        )

    def test_no_failures(self, full_test_result):
        """No test failures in the run."""
        failed_match = re.search(r"Failed:\s*(\d+)", full_test_result.stdout)
        if failed_match:
            assert int(failed_match.group(1)) == 0, (
                f"Expected 0 failures but found {failed_match.group(1)}.\n"
                f"STDOUT:\n{full_test_result.stdout[-3000:]}"
            )

    def test_discovers_335_tests(self, full_test_result):
        """NUnit discovers all 335 test cases."""
        match = re.search(r"discovered (\d+) of (\d+)", full_test_result.stdout)
        assert match, (
            f"Could not find test discovery count.\nSTDOUT:\n{full_test_result.stdout[-3000:]}"
        )
        discovered = int(match.group(1))
        assert discovered >= 335, (
            f"Expected at least 335 tests discovered but found {discovered}"
        )


class TestBouncyCastleRemoved:
    """Verify Portable.BouncyCastle has been removed from Smartstore.Core.csproj."""

    def test_no_bouncycastle_in_core_csproj(self):
        """Smartstore.Core.csproj does not reference Portable.BouncyCastle."""
        content = read_file(os.path.join("src", "Smartstore.Core", "Smartstore.Core.csproj"))
        assert "BouncyCastle" not in content, (
            "Smartstore.Core.csproj still references BouncyCastle - should have been removed"
        )

    def test_no_bouncycastle_in_base_csproj(self):
        """Smartstore.csproj does not reference Portable.BouncyCastle."""
        content = read_file(os.path.join("src", "Smartstore", "Smartstore.csproj"))
        assert "BouncyCastle" not in content, (
            "Smartstore.csproj still references BouncyCastle"
        )


class TestPreMailerNetCleanup:
    """Verify PreMailer.Net dead reference removed from base Smartstore.csproj."""

    def test_no_premailer_in_base_csproj(self):
        """Smartstore.csproj does not reference PreMailer.Net (dead reference removed)."""
        content = read_file(os.path.join("src", "Smartstore", "Smartstore.csproj"))
        assert "PreMailer.Net" not in content, (
            "Smartstore.csproj still references PreMailer.Net - dead reference should be removed"
        )

    def test_premailer_still_in_core_csproj(self):
        """Smartstore.Core.csproj still references PreMailer.Net (it is actually used there)."""
        content = read_file(os.path.join("src", "Smartstore.Core", "Smartstore.Core.csproj"))
        assert "PreMailer.Net" in content, (
            "Smartstore.Core.csproj should still reference PreMailer.Net - it is used in MessageFactory.cs"
        )


class TestJsMinReplacedWithNUglify:
    """Verify DouglasCrockford.JsMin replaced with NUglify across all usage sites."""

    def test_no_jsmin_in_any_csproj(self):
        """No csproj file references DouglasCrockford.JsMin."""
        for csproj_rel in [
            os.path.join("src", "Smartstore", "Smartstore.csproj"),
            os.path.join("src", "Smartstore.Core", "Smartstore.Core.csproj"),
            os.path.join("src", "Smartstore.Web.Common", "Smartstore.Web.Common.csproj"),
        ]:
            content = read_file(csproj_rel)
            assert "DouglasCrockford" not in content, (
                f"{csproj_rel} still references DouglasCrockford.JsMin"
            )
            assert "JsMin" not in content or "NUglifyJsMin" in content, (
                f"{csproj_rel} still references JsMin package"
            )

    def test_jsminprocessor_removed(self):
        """JsMinProcessor.cs no longer exists in the codebase."""
        processor_path = os.path.join(
            SMARTSTORE_ROOT, "src", "Smartstore.Web.Common",
            "Bundling", "Processors", "JsMinProcessor.cs"
        )
        assert not os.path.exists(processor_path), (
            "JsMinProcessor.cs should have been removed after JsMin consolidation"
        )

    def test_nuglify_in_web_common_csproj(self):
        """Smartstore.Web.Common.csproj references NUglify."""
        content = read_file(os.path.join("src", "Smartstore.Web.Common", "Smartstore.Web.Common.csproj"))
        assert "NUglify" in content, (
            "Smartstore.Web.Common.csproj should reference NUglify for JS/CSS minification"
        )

    def test_nuglify_in_google_analytics_csproj(self):
        """Smartstore.Google.Analytics.csproj references NUglify."""
        content = read_file(os.path.join(
            "src", "Smartstore.Modules", "Smartstore.Google.Analytics",
            "Smartstore.Google.Analytics.csproj"
        ))
        assert "NUglify" in content, (
            "Smartstore.Google.Analytics.csproj should reference NUglify"
        )

    def test_bundle_uses_nuglify_processor(self):
        """Bundle.cs uses NUglifyJsMinProcessor instead of JsMinProcessor."""
        content = read_file(os.path.join(
            "src", "Smartstore.Web.Common", "Bundling", "Bundle.cs"
        ))
        assert "NUglifyJsMinProcessor" in content, (
            "Bundle.cs should use NUglifyJsMinProcessor"
        )
        assert "JsMinProcessor.Instance" not in content or "NUglifyJsMinProcessor" in content, (
            "Bundle.cs should not reference old JsMinProcessor"
        )

    def test_minify_tag_helper_uses_nuglify(self):
        """MinifyTagHelper.cs uses NUglifyJsMinProcessor for inline script minification."""
        content = read_file(os.path.join(
            "src", "Smartstore.Web.Common", "TagHelpers", "Shared", "MinifyTagHelper.cs"
        ))
        assert "NUglifyJsMinProcessor" in content, (
            "MinifyTagHelper.cs should use NUglifyJsMinProcessor"
        )

    def test_google_analytics_viewcomponent_uses_nuglify(self):
        """GoogleAnalyticsViewComponent.cs uses NUglify's Uglify.Js()."""
        content = read_file(os.path.join(
            "src", "Smartstore.Modules", "Smartstore.Google.Analytics",
            "Components", "GoogleAnalyticsViewComponent.cs"
        ))
        assert "Uglify.Js(" in content, (
            "GoogleAnalyticsViewComponent.cs should use Uglify.Js() from NUglify"
        )

    def test_google_analytics_events_uses_nuglify(self):
        """Events.cs uses NUglify's Uglify.Js() for script minification."""
        content = read_file(os.path.join(
            "src", "Smartstore.Modules", "Smartstore.Google.Analytics", "Events.cs"
        ))
        assert "Uglify.Js(" in content, (
            "Events.cs should use Uglify.Js() from NUglify"
        )

    def test_nuglify_js_processor_exists(self):
        """NUglifyJsMinProcessor.cs exists as the replacement for JsMinProcessor."""
        processor_path = os.path.join(
            SMARTSTORE_ROOT, "src", "Smartstore.Web.Common",
            "Bundling", "Processors", "NUglifyJsMinProcessor.cs"
        )
        assert os.path.exists(processor_path), (
            "NUglifyJsMinProcessor.cs must exist as the JsMin replacement"
        )

    def test_nuglify_css_processor_exists(self):
        """NUglifyCssMinProcessor.cs exists for CSS minification."""
        processor_path = os.path.join(
            SMARTSTORE_ROOT, "src", "Smartstore.Web.Common",
            "Bundling", "Processors", "NUglifyCssMinProcessor.cs"
        )
        assert os.path.exists(processor_path), (
            "NUglifyCssMinProcessor.cs must exist for CSS minification"
        )


class TestAddRangeOptimization:
    """Verify TargetGroupEvaluatorTask uses AddRange for bulk inserts."""

    def test_uses_addrange_not_individual_add(self):
        """TargetGroupEvaluatorTask.cs uses AddRange() instead of individual Add() calls."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        assert "AddRange(" in content, (
            "TargetGroupEvaluatorTask.cs should use AddRange() for bulk inserts"
        )

    def test_addrange_on_customer_role_mappings(self):
        """AddRange is called on _db.CustomerRoleMappings."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        assert "_db.CustomerRoleMappings.AddRange(" in content, (
            "AddRange should be called on _db.CustomerRoleMappings"
        )

    def test_chunk_size_500_preserved(self):
        """The 500-record chunk size is preserved in the AddRange optimization."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        assert "Chunk(500)" in content or ".Chunk(500)" in content, (
            "TargetGroupEvaluatorTask.cs should chunk records in groups of 500"
        )

    def test_commit_after_each_chunk(self):
        """CommitAsync is still called after each chunk insertion."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        assert "scope.CommitAsync(" in content, (
            "TargetGroupEvaluatorTask.cs should still call CommitAsync after each chunk"
        )

    def test_is_system_mapping_flag_set(self):
        """IsSystemMapping = true is set on new mappings."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        assert "IsSystemMapping = true" in content, (
            "New CustomerRoleMapping records must have IsSystemMapping = true"
        )

    def test_no_individual_add_calls(self):
        """No individual _db.CustomerRoleMappings.Add() calls (replaced by AddRange)."""
        content = read_file(os.path.join(
            "src", "Smartstore.Core", "Platform", "Identity", "Rules",
            "TargetGroupEvaluatorTask.cs"
        ))
        # There should be AddRange but not a standalone .Add( call
        lines = content.split("\n")
        for line in lines:
            stripped = line.strip()
            if "_db.CustomerRoleMappings.Add(" in stripped and "AddRange" not in stripped:
                pytest.fail(
                    f"Found individual Add() call that should be AddRange(): {stripped}"
                )


class TestNUnitTestSuiteIntegrity:
    """Verify the existing NUnit test suite passes with milestone 3 changes."""

    def test_targetgroup_unit_tests_pass(self):
        """All Tier 1 TargetGroupEvaluatorTask unit tests pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Unit tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )

    def test_targetgroup_integration_tests_pass(self):
        """All Tier 2 TargetGroupEvaluatorTask integration tests pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskIntegrationTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Integration tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )

    def test_targetgroup_parity_tests_pass(self):
        """All Tier 3 TargetGroupEvaluatorTask parity tests pass."""
        result = run_dotnet_test(
            "FullyQualifiedName~TargetGroupEvaluatorTaskParityTests",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Parity tests failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )

    def test_chunk_size_integration_test_passes(self):
        """The chunk_size_500_honored integration test passes (validates AddRange behavior)."""
        result = run_dotnet_test(
            "FullyQualifiedName~Chunk_size_500_honored",
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Chunk size test failed.\nSTDOUT:\n{result.stdout[-2000:]}"
        )
