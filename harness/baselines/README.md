# Baselines

Accepted values for baseline-mode rules, one YAML list per file. Each entry is a rule ID, a network
profile, a value, the run it came from (`source_run_id`) and when it was set (`set_at`, UTC).

Baselines change only through a commit to this folder, never automatically (spec 014, FR-014). Until
an entry exists for a rule and profile, that rule only reports. Gate 9 records the first ones.

```yaml
- rule_id: bot.correction_p99
  profile: Typical
  value: 0.42
  source_run_id: bot-typical-20261201-abc123
  set_at: 2026-12-01T00:00:00Z
```
