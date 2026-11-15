use std::{env, fs, path::PathBuf, process::Command};

fn main() {
    println!("cargo:rerun-if-changed=../../assets/icon.ico");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let output = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    let icon = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap())
        .join("../../assets/icon.ico")
        .canonicalize()
        .unwrap();
    let resource = output.join("openprism.rc");
    fs::write(
        &resource,
        format!(
            "#pragma code_page(65001)\n1 ICON \"{}\"\n",
            icon.to_string_lossy().replace('\\', "/")
        ),
    )
    .unwrap();
    let compiled = output.join("openprism.res");
    let kits = PathBuf::from(
        env::var_os("ProgramFiles(x86)").unwrap_or_else(|| "C:/Program Files (x86)".into()),
    )
    .join("Windows Kits/10/bin");
    let mut versions: Vec<_> = fs::read_dir(kits)
        .expect("Windows SDK resource compiler is required")
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.join("x64/rc.exe").exists())
        .collect();
    versions.sort();
    let compiler = versions
        .last()
        .expect("Windows SDK x64 rc.exe is required")
        .join("x64/rc.exe");
    let status = Command::new(compiler)
        .arg("/nologo")
        .arg("/fo")
        .arg(&compiled)
        .arg(resource)
        .status()
        .expect("run Windows resource compiler");
    assert!(status.success(), "resource compilation failed");
    println!(
        "cargo:rustc-link-arg-bin=wgtun-native={}",
        compiled.display()
    );
}
