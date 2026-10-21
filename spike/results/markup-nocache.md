## markup-nocache

Cells: keys missing from the expected set, then `+n` extra keys (exact requests only).
`resolved` and `refs2` list counts. Rich text shape is read from the body.

| request | ran | rich text | render | objects | json | refs | refsCulture | resolved | refs2 |
|---|---|---|---|---|---|---|---|---|---|
| route-cold | True | markup | all | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| id-cold | True | markup | all | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| ids-cold | True | markup | all | missing 1/12 | missing 1/12 | all | all | 2 | 1 |
| query-cold | True | markup | all | missing 1/11 | missing 1/11 | all | all | 9 | 1 |
| expand-cold | True | markup | all | missing 1/12 | missing 1/12 | missing 1/12 | missing 1/12 | 1 | 1 |
| preview-cold | True | markup | all | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| media-cold | True | none | all | all | all | all | all | 1 | 0 |
| variant-da-cold | True | none | all | all | all | all, +1 | all | 1 | 0 |
| variant-en-cold | True | none | all | all | all | all, +1 | all | 1 | 0 |
| route-warm | True | markup | all | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| id-warm | True | markup | missing 8/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| ids-warm | True | markup | missing 7/12 | missing 1/12 | missing 1/12 | all | all | 2 | 1 |
| query-warm | True | markup | missing 5/11 | missing 1/11 | missing 1/11 | all | all | 9 | 1 |
| expand-warm | True | markup | missing 8/12 | missing 1/12 | missing 1/12 | missing 1/12 | missing 1/12 | 1 | 1 |
| preview-warm | True | markup | all | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| media-warm | True | none | all | all | all | all | all | 1 | 0 |
| variant-da-warm | True | none | all | all | all | all, +1 | all | 1 | 0 |
| variant-en-warm | True | none | missing 1/2 | all | all | all, +1 | all | 1 | 0 |
| route-after-publish-a | True | markup | missing 1/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| id-after-publish-a | True | markup | missing 8/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| route-after-publish-f | True | markup | missing 1/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| id-after-publish-f | True | markup | missing 8/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| route-after-save-m1 | True | markup | missing 1/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |
| id-after-save-m1 | True | markup | missing 8/11 | missing 1/11 | missing 1/11 | all | all | 1 | 1 |

### Child of C, renamed parent

- route before rename: `/target-c/child-of-c/`, warm: `/target-c/child-of-c/`
- route served after renaming C: `/target-c-201818188/child-of-c/`; current route (preview): `/target-c-201818188/child-of-c/`
- controller ran after rename: True

### Headers

- route-cold: {"Cache-Control": "", "Vary": "Accept-Language, Accept-Segment, Preview, Start-Item", "X-Spike-Preview": "False", "X-Spike-Culture": "en-US"}
- preview-cold: {"Cache-Control": "", "Vary": "Accept-Language, Accept-Segment, Preview, Start-Item", "X-Spike-Preview": "True", "X-Spike-Culture": "en-US"}
- variant-da-cold: {"Cache-Control": "", "Vary": "Accept-Language, Accept-Segment, Preview, Start-Item", "X-Spike-Preview": "False", "X-Spike-Culture": "da-DK"}
- route-warm: {"Cache-Control": "", "Vary": "Accept-Language, Accept-Segment, Preview, Start-Item", "X-Spike-Preview": "False", "X-Spike-Culture": "en-US"}

### Differences per strategy

- route-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- route-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- id-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- id-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- ids-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- ids-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- query-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- query-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-cold refs: missing ['c:10000000-0000-0000-0000-000000000010']
- expand-cold refsCulture: missing ['c:10000000-0000-0000-0000-000000000010']
- preview-cold objects: missing ['m:20000000-0000-0000-0000-000000000004']
- preview-cold json: missing ['m:20000000-0000-0000-0000-000000000004']
- variant-da-cold refs: missing [], extra ['c:10000000-0000-0000-0000-00000000000b']
- variant-en-cold refs: missing [], extra ['c:10000000-0000-0000-0000-00000000000c']
- route-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- route-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- id-warm render: missing ['c:10000000-0000-0000-0000-00000000000a', 'c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- id-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- id-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- ids-warm render: missing ['c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- ids-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- ids-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- query-warm render: missing ['c:10000000-0000-0000-0000-00000000000d', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- query-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- query-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-warm render: missing ['c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'c:10000000-0000-0000-0000-000000000010', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- expand-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- expand-warm refs: missing ['c:10000000-0000-0000-0000-000000000010']
- expand-warm refsCulture: missing ['c:10000000-0000-0000-0000-000000000010']
- preview-warm objects: missing ['m:20000000-0000-0000-0000-000000000004']
- preview-warm json: missing ['m:20000000-0000-0000-0000-000000000004']
- variant-da-warm refs: missing [], extra ['c:10000000-0000-0000-0000-00000000000b']
- variant-en-warm render: missing ['c:10000000-0000-0000-0000-00000000000b']
- variant-en-warm refs: missing [], extra ['c:10000000-0000-0000-0000-00000000000c']
- route-after-publish-a render: missing ['c:10000000-0000-0000-0000-00000000000e']
- route-after-publish-a objects: missing ['m:20000000-0000-0000-0000-000000000004']
- route-after-publish-a json: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-publish-a render: missing ['c:10000000-0000-0000-0000-00000000000a', 'c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- id-after-publish-a objects: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-publish-a json: missing ['m:20000000-0000-0000-0000-000000000004']
- route-after-publish-f render: missing ['c:10000000-0000-0000-0000-00000000000e']
- route-after-publish-f objects: missing ['m:20000000-0000-0000-0000-000000000004']
- route-after-publish-f json: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-publish-f render: missing ['c:10000000-0000-0000-0000-00000000000a', 'c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- id-after-publish-f objects: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-publish-f json: missing ['m:20000000-0000-0000-0000-000000000004']
- route-after-save-m1 render: missing ['c:10000000-0000-0000-0000-00000000000e']
- route-after-save-m1 objects: missing ['m:20000000-0000-0000-0000-000000000004']
- route-after-save-m1 json: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-save-m1 render: missing ['c:10000000-0000-0000-0000-00000000000a', 'c:10000000-0000-0000-0000-00000000000d', 'c:10000000-0000-0000-0000-00000000000e', 'c:10000000-0000-0000-0000-00000000000f', 'm:20000000-0000-0000-0000-000000000001', 'm:20000000-0000-0000-0000-000000000002', 'm:20000000-0000-0000-0000-000000000003', 'm:20000000-0000-0000-0000-000000000004']
- id-after-save-m1 objects: missing ['m:20000000-0000-0000-0000-000000000004']
- id-after-save-m1 json: missing ['m:20000000-0000-0000-0000-000000000004']
