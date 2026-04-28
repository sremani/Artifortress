# Phase 6 RPO/RTO Drill Report

Generated at: 2026-04-28T03:19:22Z

## Inputs

- source DB: `artifortress`
- drill DB: `artifortress_drill`
- backup file: `/tmp/artifortress-phase6-drill-20260427-221917.sql`
- RTO target (seconds): 900
- RPO target (seconds): 300

## Results

- backup duration (seconds): 0
- restore duration (seconds): 4
- total drill duration (seconds): 5
- RPO status: PASS
- RTO status: PASS
- data verification: PASS

## Verification Notes

- all required table counts matched between source and drill databases.
