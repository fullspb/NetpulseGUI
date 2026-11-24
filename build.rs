use std::path::PathBuf;

fn main() {
    let config = slint_build::CompilerConfiguration::new()
        .with_style("fluent".into())
        .with_default_translation_context(slint_build::DefaultTranslationContext::None)
        .with_bundled_translations("lang");
    slint_build::compile_with_config("ui/app.slint", config).expect("Slint build failed");

    // Ресурсы Windows: значок, манифест (права администратора, DPI), сведения о версии.
    println!("cargo:rerun-if-changed=assets/app.rc");
    println!("cargo:rerun-if-changed=assets/app.manifest");
    println!("cargo:rerun-if-changed=assets/netpulse.ico");
    println!("cargo:rerun-if-changed=lang");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let out = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("app_res.o");
        let status = std::process::Command::new("windres")
            .args(["--input", "assets/app.rc", "--output-format=coff", "--include-dir", "assets", "--output"])
            .arg(&out)
            .status()
            .expect("windres not found (MinGW binutils must be on PATH)");
        assert!(status.success(), "windres failed");
        println!("cargo:rustc-link-arg-bins={}", out.display());

        // MinGW сам подкладывает default-manifest.o (asInvoker), и он спорит с нашим манифестом.
        // Подменяем его пустым объектом: gcc ищет файл сначала в каталогах из -B.
        let dir = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("nomanifest");
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("empty.c");
        std::fs::write(&src, "").unwrap();
        let status = std::process::Command::new("gcc")
            .args(["-c", "-x", "c"])
            .arg(&src)
            .arg("-o")
            .arg(dir.join("default-manifest.o"))
            .status()
            .expect("gcc not found");
        assert!(status.success(), "gcc failed");
        println!("cargo:rustc-link-arg-bins=-B{}", dir.display());
    }
}
