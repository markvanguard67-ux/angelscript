"""Portable tests of the Unity XML gate; these do not execute Unity or Burst."""
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from verify_unity_results import FIXTURE, REQUIRED_TESTS, verify


class UnityResultGateTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "results.xml"

    def write_results(self, root_result="Passed", omitted=(), outcomes=None):
        root = ET.Element("test-run", result=root_result)
        for name in REQUIRED_TESTS:
            if name not in omitted:
                ET.SubElement(root, "test-case", fullname=FIXTURE + name,
                              result=(outcomes or {}).get(name, "Passed"))
        ET.ElementTree(root).write(self.path)

    def test_all_real_probes_and_integration_cases_pass(self):
        self.write_results()
        self.assertEqual(len(REQUIRED_TESTS), verify(self.path))

    def test_missing_file_is_not_success(self):
        with self.assertRaises(ValueError):
            verify(self.path)

    def test_malformed_or_empty_file_is_not_success(self):
        for contents in ("", "<test-run>"):
            self.path.write_text(contents)
            with self.assertRaises(ValueError):
                verify(self.path)

    def test_zero_tests_is_not_success(self):
        self.path.write_text('<test-run result="Passed" total="0" />')
        with self.assertRaises(ValueError):
            verify(self.path)

    def test_missing_required_test_is_not_success(self):
        self.write_results(omitted=(REQUIRED_TESTS[0],))
        with self.assertRaises(ValueError):
            verify(self.path)

    def test_skipped_burst_probe_is_not_verification(self):
        for name in REQUIRED_TESTS[:2]:
            self.write_results(outcomes={name: "Skipped"})
            with self.assertRaises(ValueError):
                verify(self.path)

    def test_failed_root_or_case_is_not_success(self):
        self.write_results(root_result="Failed")
        with self.assertRaises(ValueError):
            verify(self.path)
        self.write_results(outcomes={REQUIRED_TESTS[2]: "Failed"})
        with self.assertRaises(ValueError):
            verify(self.path)

    def test_unrelated_tests_do_not_replace_required_fixture(self):
        self.write_results()
        tree = ET.parse(self.path)
        for case in tree.getroot().iter("test-case"):
            case.set("fullname", "Unrelated.Tests." + case.get("fullname"))
        tree.write(self.path)
        with self.assertRaises(ValueError):
            verify(self.path)


if __name__ == "__main__":
    unittest.main()
