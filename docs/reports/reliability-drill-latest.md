# Reliability Drill Report

Generated at: 2026-04-28T03:19:38Z

## Summary

- overall status: PASS
- total duration (seconds): 8

## Drill Results

- PASS: P4-04 outbox sweep enqueues search index job and marks event delivered
- PASS: ER-3-01 duplicate publish replay preserves single search document and job record
- PASS: ER-3-01 repeated malformed publish replay does not create search jobs or documents
- PASS: ER-3-03 stale processing search job is reclaimed after lease expiry
- PASS: ER-3-03 fresh processing search job is not reclaimed before lease expiry
