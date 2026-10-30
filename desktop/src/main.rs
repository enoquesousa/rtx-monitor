#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod renderer;
mod scene;
mod telemetry;
mod thermal_overlay;

fn main() -> eframe::Result {
    let arguments: Vec<_> = std::env::args_os().collect();
    if let Some(index) = arguments
        .iter()
        .position(|argument| argument == "--render-motion-preview")
    {
        let rpm = arguments
            .iter()
            .position(|argument| argument == "--rpm")
            .and_then(|index| arguments.get(index + 1))
            .and_then(|value| value.to_str())
            .and_then(|value| value.parse::<f32>().ok())
            .unwrap_or(1100.0);
        let result = arguments
            .get(index + 1)
            .ok_or_else(|| "Informe o diretório após --render-motion-preview.".to_owned())
            .and_then(|path| renderer::render_motion_preview(std::path::Path::new(path), rpm));
        match result {
            Ok(value) => println!("{}", value),
            Err(error) => {
                eprintln!("{error}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    if let Some(index) = arguments
        .iter()
        .position(|argument| argument == "--render-preview")
    {
        let result = arguments
            .get(index + 1)
            .ok_or_else(|| "Informe o caminho PNG após --render-preview.".to_owned())
            .and_then(|path| {
                renderer::render_preview(
                    std::path::Path::new(path),
                    arguments.iter().any(|argument| argument == "--exploded"),
                )
            });
        match result {
            Ok(value) => println!("{}", value),
            Err(error) => {
                eprintln!("{error}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    if std::env::args().any(|argument| argument == "--self-check") {
        match telemetry::self_check() {
            Ok(value) => println!("{}", value),
            Err(error) => {
                eprintln!("{error}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    let options = eframe::NativeOptions {
        renderer: eframe::Renderer::Wgpu,
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title("RTX Monitor 3D")
            .with_inner_size([1540.0, 980.0])
            .with_min_inner_size([1100.0, 740.0]),
        ..Default::default()
    };
    eframe::run_native(
        "RTX Monitor 3D",
        options,
        Box::new(|cc| Ok(Box::new(app::MonitorApp::new(cc)))),
    )
}
