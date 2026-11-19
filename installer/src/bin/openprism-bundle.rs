#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::Cursor;

fn main() -> eframe::Result {
    let result = (|| -> Result<_, Box<dyn std::error::Error>> {
        let package = tempfile::Builder::new()
            .prefix("OpenPrism-Setup-")
            .tempdir()?;
        let bytes = include_bytes!(env!("OPENPRISM_BUNDLE_PATH"));
        zip::ZipArchive::new(Cursor::new(bytes))?.extract(package.path())?;
        Ok(package)
    })();
    let package = match result {
        Ok(package) => package,
        Err(_) => {
            rfd::MessageDialog::new()
                .set_title("OpenPrism")
                .set_description(
                    "Kurulum dosyaları açılamadı. Disk alanını denetleyip yeniden dene.",
                )
                .set_level(rfd::MessageLevel::Error)
                .show();
            std::process::exit(1);
        }
    };
    openprism_installer::run(package.path().to_owned(), false)
}
