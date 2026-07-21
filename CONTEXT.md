# Tbot Ultra

Tbot Ultra automates actions on Official Travian worlds while keeping account- and world-specific game state separate.

## Language

**Map Oasis Scan**:
A resumable search of Official map areas for oases matching a selected scope and filter.
_Avoid_: Map crawl, oasis scrape

**Map Oasis Checkpoint**:
The account- and world-specific saved progress and partial results of one Map Oasis Scan. It resumes a matching scan after an unexpected failure; user cancellation produces a partial result and clears the checkpoint.
_Avoid_: Scan cache, temporary scan state

**Map Oasis Scan Result**:
The list of oases found by a Map Oasis Scan together with completion information. The oasis list remains the input to Farm Lists and other downstream uses.
_Avoid_: Map data response, scan output

**Construction Queue Reconciliation**:
The application of a confirmed live village construction status to pending construction queue items, including satisfied targets, slot rebinding, and required dependencies.
_Avoid_: Queue repair, build queue sync
