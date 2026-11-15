# Native UI backend contract

The native UI expects `presets` to return objects with `id`, `engine` (`byedpi` or `zapret`), `name`, arguments and notes. Settings retain `dpi` for ByeDPI and use a separate `zapretDpi` object with `engine`, `presetId`, `dns`, and `customArguments` for Zapret. Defaults are `byedpi-standard` and `zapret-standard`. Saving one must preserve the other; the runtime must resolve the selected engine's preset.

`goodcheck` accepts `{ hosts: string[], engine: "byedpi" | "zapret" }` and returns `code`, `success`, `message`, and `results` containing `presetId`, `name`, `engine`, `successes`, `total`, and optionally `message`. Results must identify presets for the requested engine. Cancellation uses `goodcheck-cancel`.

`install-component` and `remove-component` accept a component ID as their first argument (`byedpi`, `zapret`, or `goodcheck`), report errors through the standard command response and refresh component scan state. Core service installation must not install optional engines implicitly; WGTun and sing-box are removed only through `remove-service`.

`diagnostics-export` accepts a destination path as its first argument and returns the standard command response. The backend owns the export contents and error handling.
