#!/usr/bin/env python3
"""Reject empty, failed, incomplete or managed-fallback Unity test runs."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

FIXTURE = "AngelScript.Burst.Tests.BurstIntegrationTests."
REQUIRED_TESTS = (
    "ActualBurstFunctionPointerExecutesTheCoreAndMath",
    "ParallelHostJobsActuallyRunInBurstAndKeepGlobalsPrivate",
    "JobsResumeAtInstructionBudgets",
    "OneLaneFaultDoesNotAffectOtherLanes",
    "SharedProgramCanServeConcurrentBatches",
    "HostCallsWithoutAHostFaultExplicitly",
    "DisposeWaitsForRecordedJobsAndPreventsReuse",
    "DeferredDisposalAndConstGlobalProtection",
    "OwnersCanCompletePreviouslyDeferredDisposal",
    "SynchronousDisposalWaitsAfterDeferredDisposal",
)


def verify(path):
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise ValueError("No valid Unity results XML: " + str(error)) from error
    if root.tag != "test-run" or root.get("result") != "Passed":
        raise ValueError("Unity test run did not pass (result=" + str(root.get("result")) + ").")
    cases = {
        case.get("fullname"): case
        for case in root.iter("test-case")
        if case.get("fullname", "").startswith(FIXTURE)
    }
    for name in REQUIRED_TESTS:
        case = cases.get(FIXTURE + name)
        if case is None:
            raise ValueError("Required test was not executed: " + name)
        if case.get("result") != "Passed":
            raise ValueError("Required test did not pass: " + name + " (" + str(case.get("result")) + "). Enable Burst; skipped probes are not verification.")
    for fullname, case in cases.items():
        if case.get("result") != "Passed":
            raise ValueError("Integration test did not pass: " + fullname)
    return len(cases)


def main():
    if len(sys.argv) != 2:
        print("Usage: verify_unity_results.py path/to/results.xml", file=sys.stderr)
        return 2
    try:
        count = verify(Path(sys.argv[1]))
    except ValueError as error:
        print("Unity/Burst validation failed: " + str(error), file=sys.stderr)
        return 1
    print("Verified " + str(count) + " passing Unity integration tests, including both real-Burst probes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
