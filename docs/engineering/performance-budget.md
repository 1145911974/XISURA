# Release performance and accessibility budget

## UI budget

The release candidate is measured at 1280×720, 1600×900, and 1920×1080; 125% and 150% DPI; Dark, Light, and High Contrast; keyboard-only, Narrator, and reduced-motion settings.

| Metric | Budget | Evidence |
|---|---:|---|
| UI first interactive with service running | ≤2.5 s | Release UIA trace |
| Idle UI CPU | <2% | WPR/WPA |
| Idle UI memory | <250 MB | WPR/WPA |
| Page transition | 190 ms | Motion token |
| Mode transition | 625 ms | Motion token |
| Reduced-motion transition | ≤100 ms | ReducedMotionDuration token |

## Service budget

| Metric | Budget | Evidence |
|---|---:|---|
| Average service CPU | <1% | 24-minute simulator soak |
| Service memory | <100 MB | 24-minute simulator soak |
| Foreground telemetry | 1 Hz | bounded scheduler trace |
| Tray telemetry | 0.2 Hz | bounded scheduler trace |
| Queue/memory growth | none | soak assertion |

These are release gates, not claims of production readiness. Task 21 records measured trace names and results; Task 22 user gates remain required before release status changes.
