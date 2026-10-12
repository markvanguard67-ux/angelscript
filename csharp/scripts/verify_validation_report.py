#!/usr/bin/env python3
"""Independent report gate. Recomputes success, never trusts a scene's headline booleans."""
import argparse
import json
from pathlib import Path


def verify(path, full=False, player=False):
    try:
        report = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise ValueError("Missing/invalid validation JSON: " + str(error)) from error
    if report.get("schemaVersion") != 1 or not report.get("complete") or report.get("cancelled"):
        raise ValueError("Validation run is incompatible, incomplete or cancelled.")
    environment = report.get("environment") or {}
    if not environment.get("burstEnabled"):
        raise ValueError("Burst was disabled.")
    if player and (environment.get("editor") or environment.get("backend") != "IL2CPP"):
        raise ValueError("Expected an executed IL2CPP player, not an Editor/Mono report.")
    for key in ("nativeSourceSha256", "recipeSha256", "corpusAssetSha256"):
        value = environment.get(key, "")
        if len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
            raise ValueError("Missing corpus provenance: " + key)
    rows = report.get("results") or []
    plan = report.get("plannedCaseIds") or []
    ids = [row.get("id") for row in rows]
    if len(rows) < 30 or len(ids) != len(set(ids)) or len(plan) != len(set(plan)) or set(ids) != set(plan):
        raise ValueError("Empty, duplicated or incomplete planned test execution.")
    required = [row for row in rows if row.get("requiredForSubset")]
    if len(required) < 20:
        raise ValueError("Insufficient required subset coverage.")
    probe = next((row for row in rows if row.get("id") == "integration-burst-kernel"), {})
    if probe.get("status") != "Passed" or not probe.get("burstExecuted"):
        raise ValueError("Actual Burst function-pointer execution was not proven.")
    failed_required = [row.get("id") for row in required if row.get("status") != "Passed"]
    if failed_required:
        raise ValueError("Required checks failed/blocked: " + ", ".join(failed_required))
    # Source fixtures accepted by the native engine must prove their job path too.
    for row in required:
        if row.get("source") and not str(row.get("expected", "")).startswith("CompileRejected") and not row.get("burstExecuted"):
            raise ValueError("Required oracle fixture lacks Burst execution: " + row["id"])
    gaps = [row.get("id") for row in rows if row.get("status") != "Passed"]
    if not report.get("subsetPassed"):
        raise ValueError("Scene's subset flag disagrees with the verified required results.")
    if full and (gaps or not report.get("fullPortPassed")):
        raise ValueError("Full-port selected gates have gaps: " + ", ".join(gaps))
    if not full and not any(row.get("phase") in ("Future", "Conformance") for row in rows):
        raise ValueError("Full-port gap ledger was omitted.")
    return len(rows), len(gaps)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("report")
    parser.add_argument("--full-port", action="store_true")
    parser.add_argument("--player", action="store_true")
    args = parser.parse_args()
    try:
        count, gaps = verify(args.report, args.full_port, args.player)
    except ValueError as error:
        parser.exit(1, "Validation failed: " + str(error) + "\n")
    print(f"Verified {count} executed checks; {gaps} conformance/future gaps. "
          + ("IL2CPP player evidence. " if args.player else "")
          + "Finite selected-case evidence, not a full language certificate.")


if __name__ == "__main__":
    main()
