# Sessions

**Everything else in this directory is git-ignored.**

Every run writes a session folder here containing the instrument list, the DUT option and serial,
reference lock status, environment notes, raw measurement data and a Markdown summary (M1-02).

Calibration-constant backups land in `sessions/<timestamp>/cal-backup.json` (M1-01). Those are the
only copy of the instrument's calibration outside the instrument itself and the printed card under
its top cover. **Keep them.**
