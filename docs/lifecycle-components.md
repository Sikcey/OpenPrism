# Component lifecycle integration

The backend exposes `InstallComponentAsync(string)` and `RemoveComponentAsync(string)`. Accepted IDs are exactly `byedpi` and `zapret`. Native actions `install-component` and `remove-component` should forward the ID and surface the returned `OperationResult`. Core installation downloads and validates only sing-box. Component installs download pinned assets before elevation; the elevated service setup entry point accepts only fixed component operations, verifies installation ownership, and stops the broker before changing files. Removal preserves unknown files and does not remove the shared WinDivert driver service.

The broker is restarted after component changes. Callers should refresh setup status after success. Service removal retains unknown files; saved data may remain when it cannot be safely identified as app-owned.
