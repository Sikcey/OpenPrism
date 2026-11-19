#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() -> eframe::Result {
    let uninstall_only = std::env::args_os().any(|arg| arg == "--uninstall");
    openprism_installer::launch_from_executable(uninstall_only)
}
