#[path = "../assets/windows_icon.rs"]
mod windows_icon;

fn main() {
    windows_icon::embed_icon("../assets/icon.ico");
}
