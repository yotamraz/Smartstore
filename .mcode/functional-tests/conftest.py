"""
Shared test infrastructure for Smartstore functional tests.

Provides path resolution, dotnet command helpers, and shared fixtures
used across milestone test files.
"""

import os
import subprocess
import pytest


# Resolve paths -- the Smartstore repo root
# conftest.py is at Smartstore/.mcode/functional-tests/conftest.py
# Go up 2 dirs: functional-tests/ -> .mcode/ -> Smartstore/
SMARTSTORE_ROOT = os.path.normpath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
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


@pytest.fixture(scope="module")
def full_test_result():
    """Run the full NUnit test suite once for all tests in this module."""
    result = run_dotnet(
        "test", TEST_PROJECT_REL, "--no-build",
        "--logger", '"console;verbosity=normal"',
        timeout=300,
    )
    return result
