use crate::{
    renderer::{thermal_ui_colour, THERMAL_ANCHORS_C},
    scene::{SceneState, SceneTelemetry},
    telemetry::{self, Metric, Snapshot, Telemetry},
};
use eframe::egui::{self, Color32, FontId, RichText, Stroke, Vec2};
use serde_json::Value;
use std::{path::PathBuf, time::Instant};

const BG: Color32 = Color32::from_rgb(12, 17, 22);
const PANEL: Color32 = Color32::from_rgb(18, 25, 32);
const EDGE: Color32 = Color32::from_rgb(40, 53, 62);
const TEXT: Color32 = Color32::from_rgb(221, 232, 235);
const MUTED: Color32 = Color32::from_rgb(133, 153, 164);
const GREEN: Color32 = Color32::from_rgb(158, 222, 93);
const CYAN: Color32 = Color32::from_rgb(87, 191, 216);

#[derive(Clone, Copy, PartialEq)]
enum View {
    Model,
    Sensors,
    Driver,
    History,
}
pub struct MonitorApp {
    telemetry: Telemetry,
    scene: Option<SceneState>,
    scene_error: Option<String>,
    view: View,
    search: String,
    available_only: bool,
    inspector: String,
    export_pending: bool,
    capture: Option<LiveCapture>,
}

struct LiveCapture {
    path: PathBuf,
    started: Instant,
    requested: bool,
    metadata: Option<Value>,
}

impl LiveCapture {
    fn from_arguments() -> Option<Self> {
        let arguments: Vec<_> = std::env::args_os().collect();
        let index = arguments
            .iter()
            .position(|argument| argument == "--capture-live-window")?;
        Some(Self {
            path: PathBuf::from(arguments.get(index + 1)?),
            started: Instant::now(),
            requested: false,
            metadata: None,
        })
    }
}

impl MonitorApp {
    pub fn new(cc: &eframe::CreationContext<'_>) -> Self {
        let mut style = (*cc.egui_ctx.style()).clone();
        style.visuals = egui::Visuals::dark();
        style.visuals.panel_fill = BG;
        style.visuals.window_fill = PANEL;
        style.visuals.extreme_bg_color = Color32::from_rgb(9, 14, 19);
        style.visuals.faint_bg_color = Color32::from_rgb(23, 32, 40);
        style.visuals.override_text_color = Some(TEXT);
        style.visuals.selection.bg_fill = Color32::from_rgb(47, 77, 48);
        style.visuals.widgets.inactive.bg_fill = Color32::from_rgb(24, 34, 43);
        style.visuals.widgets.inactive.bg_stroke = Stroke::new(1.0_f32, EDGE);
        style.visuals.widgets.hovered.bg_fill = Color32::from_rgb(39, 56, 62);
        style.visuals.widgets.active.bg_fill = Color32::from_rgb(45, 77, 49);
        style.spacing.item_spacing = Vec2::new(10.0, 9.0);
        style.spacing.button_padding = Vec2::new(13.0, 8.0);
        style
            .text_styles
            .insert(egui::TextStyle::Body, FontId::proportional(14.0));
        style
            .text_styles
            .insert(egui::TextStyle::Heading, FontId::proportional(23.0));
        style
            .text_styles
            .insert(egui::TextStyle::Small, FontId::proportional(11.0));
        style
            .text_styles
            .insert(egui::TextStyle::Button, FontId::proportional(13.0));
        cc.egui_ctx.set_style(style);
        let result = cc
            .wgpu_render_state
            .as_ref()
            .ok_or_else(|| "Renderizador WGPU não disponível.".into())
            .and_then(SceneState::new);
        let (scene, scene_error) = match result {
            Ok(s) => (Some(s), None),
            Err(e) => (None, Some(e)),
        };
        Self {
            telemetry: Telemetry::start(),
            scene,
            scene_error,
            view: View::Model,
            search: String::new(),
            available_only: false,
            inspector: String::new(),
            export_pending: false,
            capture: LiveCapture::from_arguments(),
        }
    }

    // Capture our own rendered framebuffer through eframe. This includes the
    // actual native viewport and egui overlays without reading the desktop or
    // requiring focus. Acquisition and rendering follow the ordinary app path.
    fn capture_live_window(&mut self, ctx: &egui::Context, snapshot: &Snapshot) {
        let Some(mut capture) = self.capture.take() else {
            return;
        };
        let image = ctx.input(|input| {
            input.events.iter().find_map(|event| {
                if let egui::Event::Screenshot { image, .. } = event {
                    Some(image.clone())
                } else {
                    None
                }
            })
        });
        if let Some(image) = image {
            let result = (|| -> Result<(), String> {
                if let Some(parent) = capture
                    .path
                    .parent()
                    .filter(|path| !path.as_os_str().is_empty())
                {
                    std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
                }
                let pixels: Vec<u8> = image
                    .pixels
                    .iter()
                    .flat_map(|pixel| pixel.to_array())
                    .collect();
                let file =
                    std::fs::File::create(&capture.path).map_err(|error| error.to_string())?;
                let mut encoder = png::Encoder::new(
                    std::io::BufWriter::new(file),
                    image.size[0] as u32,
                    image.size[1] as u32,
                );
                encoder.set_color(png::ColorType::Rgba);
                encoder.set_depth(png::BitDepth::Eight);
                let mut writer = encoder.write_header().map_err(|error| error.to_string())?;
                writer
                    .write_image_data(&pixels)
                    .map_err(|error| error.to_string())?;
                writer.finish().map_err(|error| error.to_string())?;
                let mut metadata = capture.metadata.take().unwrap_or_default();
                metadata["image_size"] = serde_json::json!(image.size);
                metadata["file"] = serde_json::json!(capture.path);
                metadata["capture_source"] =
                    serde_json::json!("eframe_native_framebuffer_with_live_telemetry");
                std::fs::write(
                    capture.path.with_extension("json"),
                    serde_json::to_vec_pretty(&metadata).map_err(|error| error.to_string())?,
                )
                .map_err(|error| error.to_string())?;
                println!("{}", metadata);
                Ok(())
            })();
            if let Err(error) = result {
                eprintln!("Captura nativa: {error}");
            }
            return;
        }
        if capture.started.elapsed().as_secs() > 90 {
            eprintln!(
                "Captura nativa: tempo limite aguardando temperaturas atuais ou framebuffer."
            );
            return;
        }
        if !capture.requested
            && capture.started.elapsed().as_secs() >= 4
            && snapshot.connected
            && snapshot.metric("gpu_die_temperature_c").is_some()
            && snapshot.metric("gpu_hotspot_temperature_c").is_some()
            && (0..9).all(|index| {
                snapshot
                    .metric(&format!("thermal_channel_{index}_temperature_c"))
                    .is_some()
            })
        {
            capture.metadata = Some(serde_json::json!({
                "version": env!("CARGO_PKG_VERSION"), "requested_at_unix_ms": telemetry::now_ms(),
                "gpu": snapshot.gpu, "connected": snapshot.connected,
                "gpu_temperature_c": snapshot.metric("gpu_die_temperature_c"),
                "gpu_hotspot_temperature_c": snapshot.metric("gpu_hotspot_temperature_c"),
                "fan_rpm": snapshot.fans(), "research_observed_at": snapshot.research["observed_at"],
                "thermal_channels": (0..9).map(|index| snapshot.metric(&format!("thermal_channel_{index}_temperature_c"))).collect::<Vec<_>>(),
                "gpu_configuration_writes": false, "synthetic_telemetry": false,
                "scene_frame_interval_ms": self.scene.as_ref().map(|scene| scene.frame_time_ms),
            }));
            ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(Default::default()));
            capture.requested = true;
        }
        self.capture = Some(capture);
    }

    fn header(&mut self, ctx: &egui::Context, s: &Snapshot) {
        egui::TopBottomPanel::top("header")
            .exact_height(78.0)
            .frame(egui::Frame::new().fill(PANEL).inner_margin(18))
            .show(ctx, |ui| {
                ui.horizontal_centered(|ui| {
                    ui.label(RichText::new("R").color(GREEN).size(31.0).strong());
                    ui.vertical(|ui| {
                        ui.label(RichText::new("RTX MONITOR  /  3D").size(19.0).strong());
                        ui.label(
                            RichText::new("GALAX · GEFORCE RTX 3060")
                                .small()
                                .color(MUTED),
                        );
                    });
                    ui.add_space(30.0);
                    ui.label(
                        RichText::new(if s.connected {
                            "●  LEITURAS ATUAIS"
                        } else {
                            "●  CONECTANDO"
                        })
                        .color(if s.connected { GREEN } else { MUTED })
                        .small(),
                    );
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui
                            .add_enabled(s.connected, egui::Button::new("Exportar snapshot JSON"))
                            .clicked()
                        {
                            self.telemetry.export();
                            self.export_pending = true;
                        }
                        ui.label(
                            RichText::new("NATIVO  ·  SOMENTE LEITURA")
                                .small()
                                .color(MUTED),
                        );
                    });
                });
            });
    }
    fn sidebar(&mut self, ctx: &egui::Context, s: &Snapshot) {
        egui::SidePanel::left("navigation")
            .exact_width(195.0)
            .resizable(false)
            .frame(egui::Frame::new().fill(PANEL).inner_margin(16))
            .show(ctx, |ui| {
                ui.add_space(12.0);
                eyebrow(ui, "EXPLORAR A PLACA");
                ui.add_space(8.0);
                for (view, text) in [
                    (View::Model, "01   Placa em 3D"),
                    (View::Sensors, "02   Todas as leituras"),
                    (View::Driver, "03   Driver e pesquisa"),
                    (View::History, "04   Histórico público"),
                ] {
                    let selected = self.view == view;
                    if ui
                        .add_sized(
                            [160.0, 45.0],
                            egui::Button::new(RichText::new(text).color(if selected {
                                GREEN
                            } else {
                                TEXT
                            }))
                            .fill(if selected {
                                Color32::from_rgb(31, 51, 35)
                            } else {
                                PANEL
                            }),
                        )
                        .clicked()
                    {
                        self.view = view;
                        if view == View::Driver {
                            self.telemetry.load_full();
                        }
                        if view == View::History {
                            self.telemetry.load_history();
                        }
                    }
                }
                ui.add_space(25.0);
                ui.separator();
                ui.add_space(16.0);
                eyebrow(ui, "DISPOSITIVO");
                ui.label(RichText::new("GeForce RTX 3060").strong());
                ui.label(
                    RichText::new(format!(
                        "Driver {}",
                        string_or(&s.gpu["driver_version"], "—")
                    ))
                    .small()
                    .color(MUTED),
                );
                ui.label(
                    RichText::new(string_or(
                        &s.public["board"]["pci_bus_id"],
                        "Identificando PCI…",
                    ))
                    .small()
                    .monospace()
                    .color(MUTED),
                );
                ui.add_space(20.0);
                let done = s.research["collection"]["completed_suites"]
                    .as_u64()
                    .unwrap_or(0);
                let total = s.research["collection"]["expected_suites"]
                    .as_u64()
                    .unwrap_or(45);
                eyebrow(ui, "INVENTÁRIO DE LEITURA");
                ui.label(
                    RichText::new(format!("{done} / {total} grupos"))
                        .color(GREEN)
                        .size(19.0),
                );
                ui.add(
                    egui::ProgressBar::new(done as f32 / total.max(1) as f32)
                        .fill(GREEN)
                        .desired_height(3.0),
                );
                ui.label(
                    RichText::new("Atualizações independentes; cada fonte conserva seu horário.")
                        .small()
                        .color(MUTED),
                );
                ui.add_space(25.0);
                ui.label(
                    RichText::new("7 canais térmicos adicionais sem origem física comprovada.")
                        .small()
                        .color(CYAN),
                );
                ui.with_layout(egui::Layout::bottom_up(egui::Align::LEFT), |ui| {
                    ui.label(
                        RichText::new("Rust · renderização 3D nativa")
                            .small()
                            .color(MUTED),
                    );
                    ui.label(
                        RichText::new("Nenhuma configuração da GPU é alterada.")
                            .small()
                            .color(GREEN),
                    );
                });
            });
    }
    fn live_panel(&mut self, ctx: &egui::Context, s: &Snapshot) {
        egui::SidePanel::right("live-panel").exact_width(310.0).resizable(false).frame(egui::Frame::new().fill(BG).inner_margin(14)).show(ctx, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| {
                eyebrow(ui, "TELEMETRIA AO VIVO");
                metric_card(ui,"TEMPERATURA GPU", s.metric("gpu_die_temperature_c"), "°C", GREEN);
                ui.columns(2, |cols| {
                    small_value(&mut cols[0],"Máximo / hotspot",s.metric("gpu_hotspot_temperature_c"),"°C",1);
                    small_value(&mut cols[1],"Tensão do núcleo",s.metric("gpu_core_voltage_v"),"V",5);
                });
                ui.add_space(8.0); chart(ui,s);
                ui.add_space(8.0);
                ui.columns(2, |cols| {
                    small_value(&mut cols[0],"Clock gráfico",s.metric("clock_graphics_mhz"),"MHz",0);
                    small_value(&mut cols[1],"Potência",s.metric("power_instant_mw").map(|v|v/1000.0),"W",1);
                });
                ui.separator();
                let used=s.metric("memory_used_bytes");let total=s.metric("memory_total_bytes");
                ui.label(RichText::new("VRAM utilizada").small().color(MUTED));
                ui.label(format!("{} / {} GiB",fmt(used.map(|v|v/1073741824.0),2),fmt(total.map(|v|v/1073741824.0),1)));
                ui.add(egui::ProgressBar::new(used.zip(total).map(|(a,b)|(a/b)as f32).unwrap_or(0.0)).fill(CYAN).desired_height(4.0));
                ui.add_space(12.0); eyebrow(ui,"VENTOINHAS · ÍNDICES LÓGICOS");
                for (i,rpm) in s.fans().iter().enumerate() { ui.horizontal(|ui| {ui.label(format!("FAN {i}"));ui.with_layout(egui::Layout::right_to_left(egui::Align::Center),|ui|{ui.label(RichText::new(format!("{} RPM",fmt(rpm.map(|v|v as f64),0))).color(GREEN).size(21.0));});}); }
                ui.label(RichText::new("RPM informado pela NVML. Índice → posição do rotor é uma convenção visual; fase e sentido físicos não são medidos.").small().color(MUTED));
                ui.add_space(14.0); eyebrow(ui,"CANAIS TÉRMICOS LÓGICOS");
                for index in 0..9 {
                    let field=format!("thermal_channel_{index}_temperature_c");let value=s.metric(&field);
                    ui.horizontal(|ui| {
                        ui.label(RichText::new(format!("{:02}",index)).monospace().color(MUTED));
                        ui.label(RichText::new(match index {0=>"Média GPU",1=>"Máxima GPU",_=>"Origem física N/D"}).small());
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center),|ui|{ui.label(RichText::new(format!("{} °C",fmt(value,1))).color(if index<2{GREEN}else{CYAN}));});
                    });
                }
                ui.add_space(8.0);ui.label(RichText::new("Os sete canais sem localização ficam nesta lista; não são atribuídos a memórias, VRM ou dissipador no modelo.").small().color(MUTED));
            });
        });
    }
    fn model(&mut self, ui: &mut egui::Ui, s: &Snapshot) {
        let Some(scene) = &mut self.scene else {
            ui.heading("Visualização 3D indisponível");
            ui.label(self.scene_error.as_deref().unwrap_or("Falha no modelo"));
            return;
        };
        ui.horizontal(|ui| {
            ui.heading("Sua placa, por dentro.");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.button("Redefinir câmera").clicked() {
                    scene.reset_camera();
                }
                for (label, key) in [
                    ("Lateral", "side"),
                    ("Verso", "back"),
                    ("Frente", "front"),
                    ("Isométrica", "iso"),
                ] {
                    if ui.button(label).clicked() {
                        scene.set_view(key);
                    }
                }
            });
        });
        ui.label(RichText::new("Arraste para orbitar · botão direito para mover · roda para aproximar · clique em uma peça").small().color(MUTED));
        ui.add_space(7.0);
        ui.horizontal_wrapped(|ui| {
            if ui.button("Montada").clicked() {
                scene.explode = 0.0;
                scene.set_view("iso");
                scene.show_labels = true;
                scene.show_pcb = true;
                scene.show_shroud = true;
                scene.show_heatsink = true;
                scene.show_fans = true;
                scene.show_backplate = true;
            }
            if ui.button("Detalhe das ventoinhas").clicked() {
                scene.explode = 0.0;
                scene.set_view("fans");
                scene.show_labels = true;
                scene.show_pcb = true;
                scene.show_shroud = true;
                scene.show_heatsink = true;
                scene.show_fans = true;
                scene.show_backplate = true;
            }
            if ui.button("Vista explodida").clicked() {
                scene.explode = 0.75;
                scene.show_shroud = true;
                scene.show_heatsink = true;
                scene.show_fans = true;
                scene.show_backplate = true;
            }
            if ui.button("Ver região medida").clicked() {
                scene.explode = 0.0;
                scene.show_shroud = false;
                scene.show_heatsink = false;
                scene.show_fans = false;
                scene.show_backplate = false;
                scene.thermal_mode = true;
                scene.show_labels = true;
            }
            ui.add(
                egui::Slider::new(&mut scene.explode, 0.0..=1.0)
                    .text("Explosão")
                    .show_value(false),
            );
            ui.checkbox(&mut scene.thermal_mode, "Cor térmica medida");
            ui.checkbox(&mut scene.animate_fans, "Animação por RPM").on_hover_text("Em alta rotação, as pás aparecem com borrão de movimento. Desative para inspecionar seu formato; as leituras de RPM continuam ao vivo.");
        });
        if !scene.animate_fans {
            ui.label(RichText::new("Animação visual pausada para inspecionar as pás. As leituras de RPM continuam ao vivo.").small().color(MUTED));
        }
        let height = (ui.available_height() - 168.0).max(280.0);
        let thermal_metrics = s.metrics();
        let data = SceneTelemetry {
            gpu_temp: s.metric("gpu_die_temperature_c").map(|v| v as f32),
            hotspot: s.metric("gpu_hotspot_temperature_c").map(|v| v as f32),
            fan_rpm: s.fans(),
            stale: !s.connected,
            thermal_channels: std::array::from_fn(|index| {
                s.metric(&format!("thermal_channel_{index}_temperature_c"))
                    .map(|value| value as f32)
            }),
            thermal_age_seconds: thermal_metrics
                .iter()
                .find(|metric| metric.field == "thermal_channel_0_temperature_c")
                .and_then(|metric| metric.timestamp_ms)
                .map(|timestamp| (telemetry::now_ms() - timestamp).max(0) as u64 / 1000),
        };
        ui.allocate_ui(Vec2::new(ui.available_width(), height), |ui| {
            scene.show(ui, &data)
        });
        ui.add_space(6.0);
        panel(ui, |ui| {
            ui.horizontal_wrapped(|ui| {
                eyebrow(ui, "CAMADAS");
                ui.checkbox(&mut scene.show_pcb, "PCB e componentes");
                ui.checkbox(&mut scene.show_heatsink, "Dissipador");
                ui.checkbox(&mut scene.show_fans, "Ventoinhas");
                ui.checkbox(&mut scene.show_shroud, "Carenagem");
                ui.checkbox(&mut scene.show_backplate, "Backplate");
                ui.checkbox(&mut scene.show_labels, "Rótulos");
                let mut picked = None;
                egui::ComboBox::from_id_salt("model-part-selector")
                    .width(220.0)
                    .selected_text(
                        scene
                            .selected_part()
                            .map(|p| p.label.as_str())
                            .unwrap_or("Selecionar uma peça…"),
                    )
                    .show_ui(ui, |ui| {
                        for part in scene.parts() {
                            let selected = scene.selected_part().is_some_and(|p| p.id == part.id);
                            if ui.selectable_label(selected, &part.label).clicked() {
                                picked = Some(part.id.clone());
                            }
                        }
                    });
                if let Some(id) = picked {
                    scene.select_part(&id);
                }
            });
            ui.horizontal(|ui| {
                for (temperature, text) in THERMAL_ANCHORS_C.into_iter().zip([
                    "● 20 °C",
                    "● 46,25 °C",
                    "● 72,5 °C",
                    "● 95 °C",
                ]) {
                    ui.colored_label(thermal_ui_colour(temperature), text);
                }
                ui.label(
                    RichText::new("Cinza: sem medição localizada")
                        .color(MUTED)
                        .small(),
                );
            });
            if let Some(part) = scene.selected_part() {
                ui.label(RichText::new(&part.label).strong())
                    .on_hover_text(format!(
                        "Identificador do modelo: {} · objeto {}",
                        part.id, part.name
                    ));
                ui.label(RichText::new(if part.thermal_binding.is_some(){"GPU: cor pela temperatura média; posição do hotspot não publicada."}else{"Peça identificada pela referência visual. Sem temperatura física localizada comprovada."}).small().color(MUTED));
            } else {
                ui.label(RichText::new(format!("{} peças selecionáveis · dimensões aproximadas a partir das referências, sem cotas CAD",scene.part_count())).small().color(MUTED));
            }
        });
    }
    fn sensors(&mut self, ui: &mut egui::Ui, s: &Snapshot) {
        ui.heading("Todas as leituras");
        ui.label(
            RichText::new(
                "Fontes e horários originais; ausência de suporte permanece indisponível.",
            )
            .color(MUTED),
        );
        ui.horizontal(|ui| {
            ui.add(
                egui::TextEdit::singleline(&mut self.search)
                    .hint_text("Buscar campo, fonte ou unidade…")
                    .desired_width(430.0),
            );
            ui.checkbox(&mut self.available_only, "Somente disponíveis");
        });
        let search = self.search.to_lowercase();
        let metrics: Vec<_> = s
            .metrics()
            .into_iter()
            .filter(|m| {
                (!self.available_only || m.state == "available")
                    && (search.is_empty()
                        || format!("{} {} {} {}", m.label, m.field, m.source, m.unit)
                            .to_lowercase()
                            .contains(&search))
            })
            .collect();
        ui.add_space(8.0);
        egui::ScrollArea::vertical().show(ui, |ui| {
            egui::Grid::new("sensor-grid")
                .num_columns(4)
                .striped(true)
                .spacing([18.0, 12.0])
                .show(ui, |ui| {
                    for text in ["LEITURA / CAMPO", "VALOR", "FONTE / ESTADO", "AQUISIÇÃO"] {
                        ui.label(RichText::new(text).small().color(MUTED));
                    }
                    ui.end_row();
                    for metric in metrics {
                        ui.vertical(|ui| {
                            ui.label(&metric.label);
                            ui.label(
                                RichText::new(format!("{} · {}", metric.field, metric.index))
                                    .small()
                                    .monospace()
                                    .color(MUTED),
                            );
                        });
                        ui.label(
                            RichText::new(format_metric(&metric))
                                .color(if metric.state == "available" {
                                    GREEN
                                } else {
                                    MUTED
                                })
                                .size(17.0),
                        );
                        ui.vertical(|ui| {
                            ui.label(RichText::new(&metric.source).small());
                            ui.label(
                                RichText::new(state_name(&metric.state))
                                    .small()
                                    .color(MUTED),
                            );
                        });
                        ui.label(RichText::new(age(metric.timestamp_ms)).small().color(MUTED));
                        ui.end_row();
                    }
                });
            ui.add_space(18.0);
            ui.heading("Motores Windows");
            for engine in s
                .windows_metrics()
                .into_iter()
                .filter(|m| m.field.starts_with("windows_engine_"))
            {
                ui.horizontal(|ui| {
                    ui.label(&engine.label);
                    ui.label(format!("{} %", fmt(engine.value, 1)));
                    ui.label(
                        RichText::new(state_name(&engine.state))
                            .small()
                            .color(MUTED),
                    );
                });
            }
        });
    }
    fn driver(&mut self, ui: &mut egui::Ui, s: &Snapshot) {
        ui.horizontal(|ui| {
            ui.heading("Driver e leituras revisadas");
            if ui.button("Atualizar retornos completos").clicked() {
                self.telemetry.load_full();
            }
        });
        ui.label(RichText::new("Máscaras, enums e caches sem semântica física comprovada continuam brutos. Os números de IDs não representam sensores.").color(MUTED));
        let counts = &s.research["collection"]["counts"];
        ui.horizontal(|ui| {
            for (key, label) in [
                ("unique_ids_invoked", "IDs invocados"),
                ("available_output", "saídas observadas"),
                ("success_without_available_payload", "sucessos sem payload"),
            ] {
                ui.label(
                    RichText::new(format!("{} {}", counts[key].as_u64().unwrap_or(0), label))
                        .color(GREEN),
                );
            }
        });
        let Some(full) = &s.full else {
            ui.add_space(20.0);
            ui.spinner();
            ui.label("Carregando o snapshot completo do cache local…");
            return;
        };
        ui.add_space(8.0);
        egui::ScrollArea::vertical()
            .id_salt("driver-rows")
            .max_height((ui.available_height() * 0.55).max(180.0))
            .show(ui, |ui| {
                for suite in full["collection"]["suites"]
                    .as_array()
                    .into_iter()
                    .flatten()
                {
                    let name = string_or(&suite["suite"], "Leitor");
                    let state = string_or(&suite["state"], "unknown");
                    egui::CollapsingHeader::new(format!("{name}  ·  {}", state_name(state)))
                        .id_salt(name)
                        .show(ui, |ui| {
                            ui.label(
                                RichText::new(format!(
                                    "Aquisição: {}",
                                    string_or(&suite["captured_at"], "—")
                                ))
                                .small()
                                .color(MUTED),
                            );
                            if ui.small_button("Inspecionar todo este grupo").clicked() {
                                self.inspector =
                                    serde_json::to_string_pretty(suite).unwrap_or_default();
                            }
                            if let Some(error) = suite["error"].as_str() {
                                ui.colored_label(MUTED, error);
                            }
                            for (i, q) in suite["queries"]
                                .as_array()
                                .into_iter()
                                .flatten()
                                .enumerate()
                            {
                                ui.horizontal(|ui| {
                                    ui.label(
                                        RichText::new(format!("{}", q["id"]))
                                            .monospace()
                                            .color(CYAN),
                                    );
                                    ui.label(string_or(&q["name"], "Consulta"));
                                    ui.label(
                                        RichText::new(format!(
                                            "status {} · {}",
                                            q["status"],
                                            if q["available"] == true {
                                                "saída observada"
                                            } else {
                                                "indisponível"
                                            }
                                        ))
                                        .small()
                                        .color(MUTED),
                                    );
                                    if ui.small_button(format!("Ver #{i}")).clicked() {
                                        self.inspector =
                                            serde_json::to_string_pretty(q).unwrap_or_default();
                                    }
                                });
                            }
                        });
                }
            });
        ui.separator();
        eyebrow(ui, "RETORNO SELECIONADO · SNAPSHOT COM HORÁRIO ORIGINAL");
        egui::ScrollArea::both().id_salt("raw-json").show(ui, |ui| {
            ui.add(
                egui::TextEdit::multiline(&mut self.inspector)
                    .font(egui::TextStyle::Monospace)
                    .desired_width(f32::INFINITY)
                    .desired_rows(12)
                    .interactive(false),
            );
        });
    }
    fn history(&mut self, ui: &mut egui::Ui, s: &Snapshot) {
        ui.horizontal(|ui| {
            ui.heading("Histórico público");
            if ui.button("Atualizar").clicked() {
                self.telemetry.load_history();
            }
        });
        ui.label(RichText::new("Eventos públicos persistidos no SQLite. As leituras privadas ficam no snapshot atual e na exportação.").color(MUTED));
        let Some(history) = &s.history else {
            ui.spinner();
            return;
        };
        egui::ScrollArea::vertical().show(ui, |ui| {
            for item in history["items"].as_array().into_iter().flatten() {
                let event = item.get("event").unwrap_or(item);
                egui::CollapsingHeader::new(format!(
                    "#{}  {}",
                    item["event_id"],
                    string_or(&event["event_type"], "Evento")
                ))
                .id_salt(item["event_id"].to_string())
                .show(ui, |ui| {
                    ui.monospace(serde_json::to_string_pretty(item).unwrap_or_default());
                });
            }
        });
    }
}

impl eframe::App for MonitorApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        let s = self.telemetry.snapshot();
        self.header(ctx, &s);
        self.sidebar(ctx, &s);
        if self.view == View::Model {
            self.live_panel(ctx, &s);
        }
        egui::TopBottomPanel::bottom("status")
            .exact_height(33.0)
            .frame(egui::Frame::new().fill(PANEL).inner_margin(8))
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new(&s.status).small().color(if s.connected {
                        GREEN
                    } else {
                        MUTED
                    }));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.label(
                            RichText::new(format!(
                                "v{} · Windows · Rust / WGPU",
                                env!("CARGO_PKG_VERSION")
                            ))
                            .small()
                            .color(MUTED),
                        );
                    });
                });
            });
        egui::CentralPanel::default()
            .frame(egui::Frame::new().fill(BG).inner_margin(18))
            .show(ctx, |ui| {
                if let Some(error) = &s.error {
                    ui.label(
                        RichText::new(error)
                            .small()
                            .color(Color32::from_rgb(225, 169, 90)),
                    );
                }
                if let Some(path) = &s.export_path {
                    panel(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
                            ui.label(RichText::new("Snapshot salvo:").color(GREEN));
                            ui.label(path.display().to_string());
                            if ui.small_button("Abrir pasta").clicked() {
                                if let Some(parent) = path.parent() {
                                    let _ = std::process::Command::new("explorer.exe")
                                        .arg(parent)
                                        .spawn();
                                }
                            }
                        });
                    });
                } else if self.export_pending {
                    ui.label(
                        RichText::new("Preparando o arquivo JSON completo…")
                            .small()
                            .color(MUTED),
                    );
                }
                if let Some(error) = &s.export_error {
                    ui.colored_label(MUTED, error);
                    self.export_pending = false;
                }
                match self.view {
                    View::Model => self.model(ui, &s),
                    View::Sensors => self.sensors(ui, &s),
                    View::Driver => self.driver(ui, &s),
                    View::History => self.history(ui, &s),
                }
            });
        self.capture_live_window(ctx, &s);
        ctx.request_repaint_after(std::time::Duration::from_millis(
            if self.view == View::Model { 16 } else { 500 },
        ));
    }
}

fn panel(ui: &mut egui::Ui, contents: impl FnOnce(&mut egui::Ui)) {
    egui::Frame::new()
        .fill(PANEL)
        .stroke(Stroke::new(1.0_f32, EDGE))
        .corner_radius(8)
        .inner_margin(12)
        .show(ui, contents);
}
fn eyebrow(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text).small().color(MUTED));
}
fn metric_card(ui: &mut egui::Ui, title: &str, value: Option<f64>, unit: &str, colour: Color32) {
    panel(ui, |ui| {
        ui.set_min_width(ui.available_width());
        eyebrow(ui, title);
        ui.label(
            RichText::new(format!("{} {unit}", fmt(value, 1)))
                .size(39.0)
                .color(colour)
                .strong(),
        );
    });
}
fn small_value(ui: &mut egui::Ui, title: &str, value: Option<f64>, unit: &str, digits: usize) {
    ui.vertical(|ui| {
        eyebrow(ui, title);
        ui.label(
            RichText::new(format!("{} {unit}", fmt(value, digits)))
                .size(21.0)
                .strong(),
        );
    });
}
fn fmt(value: Option<f64>, digits: usize) -> String {
    value
        .map(|v| format!("{v:.digits$}").replace('.', ","))
        .unwrap_or_else(|| "—".into())
}
fn string_or<'a>(value: &'a Value, fallback: &'a str) -> &'a str {
    value.as_str().unwrap_or(fallback)
}
fn age(timestamp: Option<i64>) -> String {
    timestamp
        .map(|t| format!("há {} s", ((telemetry::now_ms() - t).max(0)) / 1000))
        .unwrap_or_else(|| "—".into())
}
fn state_name(state: &str) -> &str {
    match state {
        "available" => "Disponível",
        "not_supported" => "Sem suporte",
        "stale" => "Expirado",
        "disabled" => "Desativado",
        "unavailable" => "Indisponível",
        "query_failed" => "Falha de leitura",
        "starting" => "Iniciando",
        _ => state,
    }
}
fn format_metric(m: &Metric) -> String {
    if m.state != "available" {
        return "—".into();
    }
    match m.unit.as_str() {
        "bytes" => m
            .value
            .map(|v| format!("{} GiB", fmt(Some(v / 1073741824.0), 3)))
            .unwrap_or_else(|| format!("{} bytes", m.raw)),
        "milliwatt" => format!("{} W", fmt(m.value.map(|v| v / 1000.0), 2)),
        "celsius" | "°C" => format!("{} °C", fmt(m.value, 2)),
        "volt" | "V" => format!("{} V", fmt(m.value, 5)),
        "megahertz" => format!("{} MHz", fmt(m.value, 0)),
        _ => format!("{} {}", m.raw, m.unit),
    }
}
fn chart(ui: &mut egui::Ui, s: &Snapshot) {
    let (rect, _) =
        ui.allocate_exact_size(Vec2::new(ui.available_width(), 100.0), egui::Sense::hover());
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, 6, PANEL);
    let inner = rect.shrink2(Vec2::new(12.0, 15.0));
    for i in 0..4 {
        let y = inner.top() + i as f32 * inner.height() / 3.0;
        painter.line_segment(
            [egui::pos2(inner.left(), y), egui::pos2(inner.right(), y)],
            Stroke::new(0.5_f32, EDGE),
        );
    }
    let now = telemetry::now_ms();
    for (index, colour) in [(0, GREEN), (1, CYAN)] {
        let mut last: Option<(i64, egui::Pos2)> = None;
        for (t, core, hot) in &s.samples {
            let value = if index == 0 { core } else { hot };
            if let Some(value) = value {
                let x = inner.right() - (now - *t) as f32 / 360000.0 * inner.width();
                let y = inner.bottom()
                    - ((*value as f32 - 20.0) / 60.0).clamp(0.0, 1.0) * inner.height();
                let point = egui::pos2(x, y);
                if x >= inner.left() {
                    if let Some((previous_t, previous)) = last {
                        if *t - previous_t < 12000 {
                            painter.line_segment([previous, point], Stroke::new(1.5_f32, colour));
                        }
                    }
                    last = Some((*t, point));
                }
            } else {
                last = None;
            }
        }
    }
    painter.text(
        rect.left_top() + Vec2::new(8.0, 4.0),
        egui::Align2::LEFT_TOP,
        "GPU / máximo · últimas 6 min",
        FontId::proportional(10.0),
        MUTED,
    );
}
