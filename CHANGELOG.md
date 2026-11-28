# Changelog

All notable changes to `Glacier.Bundle` are documented here.

## v1.1.1

- Updated package dependency alignment and build validation across `Glacier.DocTree`, `Glacier.Graph`, `Glacier.Polaris`, and `Glacier.Vector`.

## v1.1.0

- **Reservation-Aware `TokenBudget` Enforcement:** `CompactingBundleBuilder` honors `TokenBudget.AvailableBudget` (`TotalBudget - ReservedTokens`) and per-section `BundleSection.StrategyOverride` across `StrictPrune`, `HeadTailTruncate`, and `ProportionalDecay`.
- **Span-Backed `IBufferWriter<char>` Output & Native AOT:** Added `CompactingBundleBuilder.WriteTo(IBufferWriter<char>)` with `<IsAotCompatible>true</IsAotCompatible>` and warning-free compilation.
- **Storage Dependency Upgrades:** Upgraded `Glacier.DocTree`, `Glacier.Graph`, and `Glacier.Vector`.
