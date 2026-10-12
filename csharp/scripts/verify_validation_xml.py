#!/usr/bin/env python3
"""Gate the laboratory Editor/PlayMode test XML; skipped or missing tests fail."""
import argparse
import xml.etree.ElementTree as ET

REQUIRED = {
    'Editor': ('ValidationHarnessTests', (
        'NativeCorpusHasProvenanceAndRealFutureFixtures',
        'EmptyIncompleteCancelledAndBlockedReportsCannotClaimSuccess',
        'SceneGeneratorCreatesSavedRunnerAndCamera',
        'ReportExportPreservesPlanAndRawBitStrings',
    )),
    'PlayMode': ('ValidationSceneTests', (
        'SceneRunsRealSupportedChecksAndReportsFullPortBlockers',
        'CancelledSceneDoesNotClaimCorrectness',
    )),
}


def verify(path, mode):
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise ValueError('Missing/invalid test XML: ' + str(error)) from error
    if root.tag != 'test-run' or root.get('result') != 'Passed': raise ValueError('Test run did not pass.')
    fixture, methods = REQUIRED[mode]
    prefix = 'AngelScript.Burst.Validation.Tests.' + fixture + '.'
    rows = {case.get('fullname'): case for case in root.iter('test-case')}
    for method in methods:
        case = rows.get(prefix + method)
        if case is None or case.get('result') != 'Passed': raise ValueError('Required test missing/failed/skipped: ' + method)
    return len(methods)


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('xml'); parser.add_argument('mode', choices=REQUIRED)
    args = parser.parse_args()
    try: count = verify(args.xml, args.mode)
    except ValueError as error: parser.exit(1, 'Validation tests failed: ' + str(error) + '\n')
    print('Verified', count, 'passing', args.mode, 'laboratory tests.')

if __name__ == '__main__': main()
