"""Portable report-gate tests. Synthetic reports are NOT Unity/Burst execution evidence."""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from verify_validation_report import verify
from verify_validation_xml import REQUIRED, verify as verify_xml
import xml.etree.ElementTree as ET


class ValidationReportGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'report.json'
        rows = [dict(id='integration-burst-kernel' if i == 0 else 'current-' + str(i), status='Passed',
                     requiredForSubset=True, phase='Current', burstExecuted=i == 0) for i in range(25)]
        rows += [dict(id='future-' + str(i), status='Blocked', requiredForSubset=False, phase='Future') for i in range(7)]
        self.report = dict(schemaVersion=1, complete=True, cancelled=False, subsetPassed=True, fullPortPassed=False,
                           environment=dict(burstEnabled=True, editor=True, backend='Mono/managed', nativeSourceSha256='a' * 64,
                                            recipeSha256='b' * 64, corpusAssetSha256='c' * 64),
                           results=rows, plannedCaseIds=[row['id'] for row in rows])

    def save(self, report=None):
        self.path.write_text(json.dumps(report or self.report))

    def test_subset_with_visible_full_port_gaps(self):
        self.save()
        self.assertEqual((32, 7), verify(self.path))
        with self.assertRaises(ValueError): verify(self.path, full=True)

    def test_empty_missing_cancelled_and_incomplete_runs_fail(self):
        with self.assertRaises(ValueError): verify(self.path)
        for changes in ({'complete': False}, {'cancelled': True}, {'results': []}, {'schemaVersion': 2}):
            report = copy.deepcopy(self.report); report.update(changes); self.save(report)
            with self.assertRaises(ValueError): verify(self.path)

    def test_booleans_do_not_override_failed_checks_or_missing_burst(self):
        for mutation in ('status', 'marker', 'enabled'):
            report = copy.deepcopy(self.report)
            if mutation == 'status': report['results'][3]['status'] = 'Failed'
            if mutation == 'marker': report['results'][0]['burstExecuted'] = False
            if mutation == 'enabled': report['environment']['burstEnabled'] = False
            self.save(report)
            with self.assertRaises(ValueError): verify(self.path)

    def test_plan_missing_or_duplicate_case_fails(self):
        report = copy.deepcopy(self.report); report['results'].pop(); self.save(report)
        with self.assertRaises(ValueError): verify(self.path)
        report = copy.deepcopy(self.report); report['results'][1]['id'] = report['results'][0]['id']; self.save(report)
        with self.assertRaises(ValueError): verify(self.path)

    def test_provenance_is_mandatory(self):
        self.report['environment']['nativeSourceSha256'] = ''; self.save()
        with self.assertRaises(ValueError): verify(self.path)

    def test_required_source_needs_burst_job_evidence(self):
        self.report['results'][1].update(source='int test(){return 1;}', expected='Finished:1', burstExecuted=False)
        self.save()
        with self.assertRaises(ValueError): verify(self.path)

    def test_il2cpp_player_gate_is_not_editor_gate(self):
        self.save()
        with self.assertRaises(ValueError): verify(self.path, player=True)
        self.report['environment'].update(editor=False, backend='IL2CPP'); self.save()
        self.assertEqual((32, 7), verify(self.path, player=True))

    def test_full_gate_requires_all_planned_capabilities_to_pass(self):
        for row in self.report['results']: row['status'] = 'Passed'
        self.report['fullPortPassed'] = True; self.save()
        self.assertEqual((32, 0), verify(self.path, full=True))


class ValidationXmlGateTests(unittest.TestCase):
    def test_complete_editor_and_playmode_fixtures(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.xml'
            for mode, (fixture, methods) in REQUIRED.items():
                root = ET.Element('test-run', result='Passed')
                for name in methods: ET.SubElement(root, 'test-case', fullname='AngelScript.Burst.Validation.Tests.' + fixture + '.' + name, result='Passed')
                ET.ElementTree(root).write(path)
                self.assertEqual(len(methods), verify_xml(path, mode))

    def test_empty_or_skipped_playmode_is_not_success(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.xml'
            path.write_text('<test-run result="Passed" />')
            with self.assertRaises(ValueError): verify_xml(path, 'PlayMode')
            fixture, methods = REQUIRED['PlayMode']; root = ET.Element('test-run', result='Passed')
            for name in methods: ET.SubElement(root, 'test-case', fullname='AngelScript.Burst.Validation.Tests.' + fixture + '.' + name, result='Skipped')
            ET.ElementTree(root).write(path)
            with self.assertRaises(ValueError): verify_xml(path, 'PlayMode')


if __name__ == '__main__': unittest.main()
