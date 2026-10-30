//! Reference-based 3D assembly. Thermal colours are restricted to the measured GPU region.
use std::{collections::HashMap, sync::Arc, time::Instant};

use eframe::{egui, egui_wgpu};
use glam::{Mat4, Vec3};
use serde::Deserialize;

use crate::renderer::{FrameData, RenderResources, SceneCallback, Vertex};

#[derive(Clone, Copy, Debug, Default)]
pub struct SceneTelemetry {
    pub gpu_temp: Option<f32>,
    pub hotspot: Option<f32>,
    pub fan_rpm: [Option<f32>; 2],
    pub stale: bool,
    pub thermal_channels: [Option<f32>; 9],
    pub thermal_age_seconds: Option<u64>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct PartInfo {
    pub id: String,
    pub node: String,
    #[serde(default)]
    pub name: String,
    pub label: String,
    pub kind: String,
    #[serde(default)]
    pub explode: [f32; 3],
    #[serde(default)]
    pub pivot: [f32; 3],
    #[serde(default)]
    pub fan_index: Option<usize>,
    #[serde(default)]
    pub thermal_binding: Option<String>,
}

#[derive(Deserialize)]
struct Manifest {
    parts: Vec<PartInfo>,
}

pub(crate) struct CpuMesh {
    pub vertices: Vec<Vertex>,
    pub indices: Vec<u32>,
    pub part: usize,
    pub colour: [f32; 4],
    pub roughness: f32,
    pub metallic: f32,
    pub min: Vec3,
    pub max: Vec3,
}

pub(crate) struct Model {
    pub meshes: Vec<CpuMesh>,
    pub parts: Vec<PartInfo>,
    pub centres: Vec<Vec3>,
    pub min: Vec3,
    pub max: Vec3,
}

/// A native WGPU viewport with a perspective camera, depth buffer and real mesh picking.
pub struct SceneState {
    pub explode: f32,
    pub thermal_mode: bool,
    pub show_pcb: bool,
    pub show_shroud: bool,
    pub show_backplate: bool,
    pub show_heatsink: bool,
    pub show_fans: bool,
    pub show_labels: bool,
    pub animate_fans: bool,
    pub frame_time_ms: f64,
    model: Arc<Model>,
    yaw: f32,
    pitch: f32,
    distance: f32,
    target: Vec3,
    selected: Option<usize>,
    fan_angles: [f64; 2],
    last_frame: Instant,
}

impl SceneState {
    pub fn new(render_state: &egui_wgpu::RenderState) -> Result<Self, String> {
        let model = Arc::new(load_model(
            include_bytes!("../assets/rtx3060.glb"),
            include_bytes!("../assets/rtx3060.parts.json"),
        )?);
        let resources = RenderResources::new(render_state, &model);
        render_state
            .renderer
            .write()
            .callback_resources
            .insert(resources);
        Ok(Self::from_model(model))
    }

    fn from_model(model: Arc<Model>) -> Self {
        let target = (model.min + model.max) * 0.5;
        let distance = (model.max - model.min).length() * 1.06;
        Self {
            explode: 0.0,
            thermal_mode: true,
            show_pcb: true,
            show_shroud: true,
            show_backplate: true,
            show_heatsink: true,
            show_fans: true,
            show_labels: false,
            animate_fans: true,
            frame_time_ms: 0.0,
            model,
            yaw: -0.22,
            pitch: 1.03,
            distance,
            target,
            selected: None,
            fan_angles: [0.0; 2],
            last_frame: Instant::now(),
        }
    }

    pub fn part_count(&self) -> usize {
        self.model.parts.len()
    }
    pub fn selected_part(&self) -> Option<&PartInfo> {
        self.selected.and_then(|index| self.model.parts.get(index))
    }
    pub fn parts(&self) -> &[PartInfo] {
        &self.model.parts
    }
    pub fn select_part(&mut self, id: &str) {
        self.selected = self
            .model
            .parts
            .iter()
            .position(|p| p.id == id || p.node == id);
    }
    pub fn reset_camera(&mut self) {
        self.yaw = -0.22;
        self.pitch = 1.03;
        self.distance = (self.model.max - self.model.min).length() * 1.06;
        self.target = (self.model.min + self.model.max) * 0.5;
    }
    pub fn set_view(&mut self, view: &str) {
        self.reset_camera();
        match view {
            "front" => {
                self.yaw = 0.0;
                self.pitch = 1.50;
            }
            "back" => {
                self.yaw = 0.0;
                self.pitch = -1.50;
            }
            "top" => {
                self.yaw = 0.0;
                self.pitch = 1.50;
            }
            "side" => {
                self.yaw = 0.0;
                self.pitch = 0.03;
            }
            "fans" => {
                self.yaw = -0.04;
                self.pitch = 1.35;
                self.distance = (self.model.max - self.model.min).length() * 0.87;
                let pivots: Vec<_> = self
                    .model
                    .parts
                    .iter()
                    .filter(|part| part.fan_index.is_some())
                    .map(|part| Vec3::from_array(part.pivot))
                    .collect();
                if !pivots.is_empty() {
                    self.target = pivots.iter().copied().sum::<Vec3>() / pivots.len() as f32;
                }
            }
            _ => {}
        }
    }

    fn camera_pose(&self, aspect: f32) -> (Mat4, Mat4, Vec3) {
        let radius = (self.model.max - self.model.min).length().max(1.0);
        let explode_min = self
            .model
            .parts
            .iter()
            .map(|part| part.explode[1])
            .fold(0.0_f32, f32::min);
        let explode_max = self
            .model
            .parts
            .iter()
            .map(|part| part.explode[1])
            .fold(0.0_f32, f32::max);
        let explosion = self.explode.clamp(0.0, 1.0);
        let target = self.target + Vec3::Y * (explode_min + explode_max) * explosion * 0.5;
        // Preserve the horizontal framing when the inspector beside the scene
        // makes the viewport narrow. This also keeps both fans in their preset.
        let aspect_scale = (1.5 / aspect.max(0.1)).max(1.0);
        let distance =
            (self.distance + (explode_max - explode_min) * explosion * 0.65) * aspect_scale;
        let eye = target
            + Vec3::new(
                self.yaw.sin() * self.pitch.cos(),
                self.pitch.sin(),
                self.yaw.cos() * self.pitch.cos(),
            ) * distance;
        let view = Mat4::look_at_rh(eye, target, Vec3::Y);
        let projection = Mat4::perspective_rh(
            40_f32.to_radians(),
            aspect.max(0.1),
            radius * 0.001,
            radius * 20.0,
        );
        (view, projection * view, eye)
    }

    pub(crate) fn preview_frame(model: Arc<Model>, size: [f32; 2], exploded: bool) -> FrameData {
        let mut state = Self::from_model(model);
        state.explode = if exploded { 0.75 } else { 0.0 };
        state.animate_fans = false;
        let (_, view_projection, eye) = state.camera_pose(size[0] / size[1].max(1.0));
        FrameData {
            size_points: size,
            view_projection: view_projection.to_cols_array_2d(),
            eye: eye.to_array(),
            transforms: (0..state.model.parts.len())
                .map(|index| state.part_transform(index))
                .collect(),
            visible: vec![true; state.model.parts.len()],
            selected: None,
            gpu_temperature: None,
            thermal_mode: false,
            fan_angular_velocity: [None; 2],
            exposure_seconds: 0.0,
        }
    }

    fn visible(&self, part: &PartInfo) -> bool {
        let kind = part.kind.to_ascii_lowercase();
        let name = part.node.to_ascii_lowercase();
        if kind.contains("backplate") || name.contains("backplate") {
            return self.show_backplate;
        }
        if kind.contains("shroud") || name.contains("shroud") {
            return self.show_shroud;
        }
        if kind.contains("fan") || name.contains("fan_") {
            return self.show_fans;
        }
        if kind.contains("heatsink")
            || kind.contains("heatpipe")
            || kind.contains("thermal_interface")
            || kind.contains("thermal_pad")
            || name.contains("heatsink")
            || name.contains("heatpipe")
            || name.contains("coldplate")
            || name.contains("fin_")
        {
            return self.show_heatsink;
        }
        self.show_pcb
    }

    fn part_transform(&self, index: usize) -> Mat4 {
        let p = &self.model.parts[index];
        let translation = Vec3::from_array(p.explode) * self.explode.clamp(0.0, 1.0);
        let mut transform = Mat4::from_translation(translation);
        if let Some(fan) = p.fan_index.filter(|&i| i < 2) {
            let pivot = Vec3::from_array(p.pivot);
            transform *= Mat4::from_translation(pivot)
                * Mat4::from_rotation_y(self.fan_angles[fan] as f32)
                * Mat4::from_translation(-pivot);
        }
        transform
    }

    pub fn show(&mut self, ui: &mut egui::Ui, telemetry: &SceneTelemetry) {
        let now = Instant::now();
        let dt = now.duration_since(self.last_frame).as_secs_f64();
        self.frame_time_ms = dt * 1000.0;
        self.last_frame = now;
        if self.animate_fans && !telemetry.stale {
            for i in 0..2 {
                if let Some(rpm) =
                    telemetry.fan_rpm[i].filter(|r| r.is_finite() && *r >= 0.0 && *r <= 30_000.0)
                {
                    self.fan_angles[i] = advance_fan_angle(self.fan_angles[i], f64::from(rpm), dt);
                }
            }
        }
        let size = ui.available_size().max(egui::vec2(120.0, 180.0));
        let (rect, response) = ui.allocate_exact_size(size, egui::Sense::click_and_drag());
        let painter = ui.painter_at(rect);
        if response.dragged_by(egui::PointerButton::Primary) {
            let delta = response.drag_motion();
            self.yaw -= delta.x * 0.008;
            self.pitch = (self.pitch + delta.y * 0.008).clamp(-1.52, 1.52);
        }
        let radius = (self.model.max - self.model.min).length().max(1.0);
        let aspect = rect.width() / rect.height().max(1.0);
        let (view, _, _) = self.camera_pose(aspect);
        if response.dragged_by(egui::PointerButton::Secondary)
            || response.dragged_by(egui::PointerButton::Middle)
        {
            let delta = response.drag_motion();
            let right = view.inverse().x_axis.truncate();
            let up = view.inverse().y_axis.truncate();
            self.target +=
                (-right * delta.x + up * delta.y) * self.distance / rect.height().max(1.0) * 0.7;
        }
        if response.hovered() {
            let scroll = ui.input(|input| input.smooth_scroll_delta.y);
            self.distance =
                (self.distance * (-scroll * 0.002).exp()).clamp(radius * 0.30, radius * 6.0);
        }
        if response.double_clicked() {
            self.reset_camera();
        }
        let (_, vp, eye) = self.camera_pose(aspect);
        let transforms: Vec<_> = (0..self.model.parts.len())
            .map(|i| self.part_transform(i))
            .collect();
        let visible: Vec<_> = self.model.parts.iter().map(|p| self.visible(p)).collect();
        if response.clicked() {
            if let Some(pointer) = response.interact_pointer_pos() {
                let x = (pointer.x - rect.left()) / rect.width() * 2.0 - 1.0;
                let y = 1.0 - (pointer.y - rect.top()) / rect.height() * 2.0;
                let inv = vp.inverse();
                let near = inv.project_point3(Vec3::new(x, y, 0.0));
                let far = inv.project_point3(Vec3::new(x, y, 1.0));
                self.selected = pick(
                    &self.model,
                    &transforms,
                    &visible,
                    near,
                    (far - near).normalize(),
                );
            }
        }
        let temperature = if telemetry.stale {
            None
        } else {
            telemetry.gpu_temp.filter(|t| t.is_finite())
        };
        let frame = Arc::new(FrameData {
            size_points: [rect.width(), rect.height()],
            view_projection: vp.to_cols_array_2d(),
            eye: eye.to_array(),
            transforms,
            visible,
            selected: self.selected,
            gpu_temperature: temperature,
            thermal_mode: self.thermal_mode,
            fan_angular_velocity: telemetry.fan_rpm.map(|rpm| {
                rpm.filter(|value| {
                    self.animate_fans
                        && !telemetry.stale
                        && value.is_finite()
                        && *value >= 0.0
                        && *value <= 30_000.0
                })
                .map(|value| value * std::f32::consts::TAU / 60.0)
            }),
            exposure_seconds: dt.min(1.0 / 30.0) as f32,
        });
        painter.add(egui_wgpu::Callback::new_paint_callback(
            rect,
            SceneCallback {
                frame: Arc::clone(&frame),
            },
        ));

        let gpu_index = self
            .model
            .parts
            .iter()
            .position(|part| part.thermal_binding.is_some());
        let gpu_anchor = gpu_index.and_then(|index| {
            project(
                vp,
                frame.transforms[index].transform_point3(self.model.centres[index]),
                rect,
            )
        });
        let gpu_outline = gpu_index.and_then(|index| {
            let (minimum, maximum) = self
                .model
                .meshes
                .iter()
                .filter(|mesh| mesh.part == index)
                .fold(
                    (Vec3::splat(f32::INFINITY), Vec3::splat(f32::NEG_INFINITY)),
                    |(minimum, maximum), mesh| (minimum.min(mesh.min), maximum.max(mesh.max)),
                );
            let corners = [
                Vec3::new(minimum.x, maximum.y, minimum.z),
                Vec3::new(maximum.x, maximum.y, minimum.z),
                Vec3::new(maximum.x, maximum.y, maximum.z),
                Vec3::new(minimum.x, maximum.y, maximum.z),
            ];
            let projected: Option<Vec<_>> = corners
                .into_iter()
                .map(|corner| project(vp, frame.transforms[index].transform_point3(corner), rect))
                .collect();
            projected.and_then(|points| points.try_into().ok())
        });
        crate::thermal_overlay::paint(
            &painter,
            rect,
            gpu_anchor,
            gpu_outline,
            telemetry,
            self.thermal_mode,
            self.show_heatsink || self.show_shroud,
        );

        let instruction =
            "Arraste: orbitar  ·  Botão direito: mover  ·  Roda: zoom  ·  Duplo clique: restaurar";
        painter.text(
            rect.left_bottom() + egui::vec2(18.0, -14.0),
            egui::Align2::LEFT_BOTTOM,
            instruction,
            egui::FontId::proportional(11.0),
            egui::Color32::from_rgb(128, 145, 148),
        );
        if self.show_labels {
            let mut fan_labelled = [false; 2];
            for (index, part) in self.model.parts.iter().enumerate() {
                let is_gpu = part.thermal_binding.as_deref() == Some("gpu_package")
                    || part.thermal_binding.as_deref() == Some("gpu");
                // GPU temperatures have their own always-visible overlay,
                // including when the measured component is behind the cooler.
                if is_gpu {
                    continue;
                }
                let is_fan = part.fan_index.filter(|i| *i < 2);
                if !frame.visible[index] || (is_fan.is_none() && self.selected != Some(index)) {
                    continue;
                }
                if let Some(fan) = is_fan {
                    if fan_labelled[fan] {
                        continue;
                    }
                    fan_labelled[fan] = true;
                }
                let world = frame.transforms[index].transform_point3(self.model.centres[index]);
                if let Some(point) = project(vp, world, rect) {
                    let label = if let Some(fan) = is_fan {
                        if telemetry.stale {
                            format!("ÍNDICE {fan} · leitura expirada")
                        } else {
                            telemetry.fan_rpm[fan]
                                .filter(|r| r.is_finite())
                                .map_or_else(
                                    || format!("ÍNDICE {fan} · RPM indisponível"),
                                    |rpm| format!("ÍNDICE {fan}  {rpm:.0} RPM"),
                                )
                        }
                    } else {
                        part.label.clone()
                    };
                    let offset = egui::vec2(0.0, -24.0);
                    label_pill(
                        &painter,
                        point,
                        offset,
                        &label,
                        egui::Color32::from_rgb(174, 192, 198),
                    );
                }
            }
        }
        if self.selected.is_some() {
            response.clone().on_hover_text(
                "Peça selecionada na geometria. A cor de seleção não representa temperatura.",
            );
        } else {
            response.clone().on_hover_text("Rotação ilustrada por índice lógico de RPM. Fase física, posição dos índices e temperatura das peças sem sensor não foram comprovadas.");
        }
        ui.ctx()
            .request_repaint_after(std::time::Duration::from_millis(16));
    }
}

pub(crate) fn advance_fan_angle(angle: f64, rpm: f64, seconds: f64) -> f64 {
    if !rpm.is_finite() || !seconds.is_finite() || rpm < 0.0 || seconds < 0.0 {
        return angle;
    }
    (angle + rpm * seconds * std::f64::consts::TAU / 60.0).rem_euclid(std::f64::consts::TAU)
}

fn label_pill(
    painter: &egui::Painter,
    point: egui::Pos2,
    offset: egui::Vec2,
    text: &str,
    colour: egui::Color32,
) {
    let galley = painter.layout_no_wrap(text.to_owned(), egui::FontId::monospace(10.0), colour);
    let centre = point + offset;
    let rect = egui::Rect::from_center_size(centre, galley.size() + egui::vec2(18.0, 10.0));
    painter.line_segment(
        [point, rect.center_bottom()],
        egui::Stroke::new(1.0_f32, colour.gamma_multiply(0.5)),
    );
    painter.circle_filled(point, 2.5, colour);
    painter.rect_filled(
        rect,
        5.0,
        egui::Color32::from_rgba_unmultiplied(9, 18, 23, 230),
    );
    painter.rect_stroke(
        rect,
        5.0,
        egui::Stroke::new(0.8_f32, colour.gamma_multiply(0.4)),
        egui::StrokeKind::Inside,
    );
    painter.galley(rect.min + egui::vec2(9.0, 5.0), galley, colour);
}

fn project(vp: Mat4, world: Vec3, rect: egui::Rect) -> Option<egui::Pos2> {
    let clip = vp * world.extend(1.0);
    if clip.w <= 0.0 {
        return None;
    }
    let ndc = clip.truncate() / clip.w;
    if ndc.z < 0.0 || ndc.z > 1.0 || ndc.x.abs() > 1.1 || ndc.y.abs() > 1.1 {
        return None;
    }
    Some(egui::pos2(
        rect.left() + (ndc.x + 1.0) * 0.5 * rect.width(),
        rect.top() + (1.0 - ndc.y) * 0.5 * rect.height(),
    ))
}

pub(crate) fn load_model(bytes: &[u8], manifest_bytes: &[u8]) -> Result<Model, String> {
    let gltf = gltf::Gltf::from_slice(bytes).map_err(|e| format!("Modelo GLB inválido: {e}"))?;
    let blob = gltf
        .blob
        .as_deref()
        .ok_or("Le modèle doit embarquer ses buffers GLB.")?;
    let manifest: Manifest = serde_json::from_slice(manifest_bytes)
        .map_err(|e| format!("Manifesto 3D inválido: {e}"))?;
    if manifest.parts.is_empty() {
        return Err("O manifesto não contém peças.".into());
    }
    let mut model = Model {
        meshes: Vec::new(),
        centres: vec![Vec3::ZERO; manifest.parts.len()],
        parts: manifest.parts,
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
    };
    let names: HashMap<_, _> = model
        .parts
        .iter()
        .enumerate()
        .map(|(i, p)| (p.node.clone(), i))
        .collect();
    let scene = gltf
        .default_scene()
        .or_else(|| gltf.scenes().next())
        .ok_or("O arquivo GLB não contém uma cena.")?;
    for node in scene.nodes() {
        load_node(node, Mat4::IDENTITY, None, blob, &names, &mut model)?;
    }
    if model.meshes.is_empty() || !model.min.is_finite() || !model.max.is_finite() {
        return Err("Geometria 3D vazia ou inválida.".into());
    }
    let mut counts = vec![0_usize; model.parts.len()];
    for mesh in &model.meshes {
        model.centres[mesh.part] += (mesh.min + mesh.max) * 0.5;
        counts[mesh.part] += 1;
    }
    for (index, count) in counts.into_iter().enumerate() {
        if count > 0 {
            model.centres[index] /= count as f32;
        }
    }
    Ok(model)
}

fn load_node(
    node: gltf::Node<'_>,
    parent: Mat4,
    inherited: Option<usize>,
    blob: &[u8],
    names: &HashMap<String, usize>,
    model: &mut Model,
) -> Result<(), String> {
    let matrix = parent * Mat4::from_cols_array_2d(&node.transform().matrix());
    let part = node
        .name()
        .and_then(|name| names.get(name).copied())
        .or(inherited);
    if let Some(mesh) = node.mesh() {
        let part = part.ok_or_else(|| {
            format!(
                "Peça GLB sem registro no manifesto: {}",
                node.name().unwrap_or("sem nome")
            )
        })?;
        for primitive in mesh.primitives() {
            if primitive.mode() != gltf::mesh::Mode::Triangles {
                continue;
            }
            let reader = primitive.reader(|buffer| match buffer.source() {
                gltf::buffer::Source::Bin => Some(blob),
                _ => None,
            });
            let positions: Vec<_> = reader
                .read_positions()
                .ok_or("Mesh sem posições.")?
                .collect();
            let indices: Vec<u32> = reader
                .read_indices()
                .map(|i| i.into_u32().collect())
                .unwrap_or_else(|| (0..positions.len() as u32).collect());
            if positions.len() > 4_000_000
                || indices.len() > 12_000_000
                || indices.iter().any(|&i| i as usize >= positions.len())
            {
                return Err("Limites de geometria excedidos ou índices inválidos.".into());
            }
            let normals: Vec<_> = reader
                .read_normals()
                .map(|n| n.collect())
                .unwrap_or_else(|| generated_normals(&positions, &indices));
            let uv: Vec<_> = reader
                .read_tex_coords(0)
                .map(|v| v.into_f32().collect())
                .unwrap_or_else(|| vec![[0.0, 0.0]; positions.len()]);
            let normal_matrix = matrix.inverse().transpose();
            let mut min = Vec3::splat(f32::INFINITY);
            let mut max = Vec3::splat(f32::NEG_INFINITY);
            let mut vertices = Vec::with_capacity(positions.len());
            for (index, position) in positions.iter().enumerate() {
                let world = matrix.transform_point3(Vec3::from_array(*position));
                let normal = normal_matrix
                    .transform_vector3(Vec3::from_array(
                        normals.get(index).copied().unwrap_or([0.0, 1.0, 0.0]),
                    ))
                    .normalize_or_zero();
                if !world.is_finite() || !normal.is_finite() {
                    return Err("Mesh contém coordenadas não finitas.".into());
                }
                min = min.min(world);
                max = max.max(world);
                vertices.push(Vertex {
                    position: world.to_array(),
                    normal: normal.to_array(),
                    uv: uv.get(index).copied().unwrap_or([0.0, 0.0]),
                });
            }
            let pbr = primitive.material().pbr_metallic_roughness();
            model.min = model.min.min(min);
            model.max = model.max.max(max);
            model.meshes.push(CpuMesh {
                vertices,
                indices,
                part,
                colour: pbr.base_color_factor(),
                roughness: pbr.roughness_factor(),
                metallic: pbr.metallic_factor(),
                min,
                max,
            });
        }
    }
    for child in node.children() {
        load_node(child, matrix, part, blob, names, model)?;
    }
    Ok(())
}

fn generated_normals(positions: &[[f32; 3]], indices: &[u32]) -> Vec<[f32; 3]> {
    let mut normals = vec![Vec3::ZERO; positions.len()];
    for triangle in indices.chunks_exact(3) {
        let a = Vec3::from_array(positions[triangle[0] as usize]);
        let b = Vec3::from_array(positions[triangle[1] as usize]);
        let c = Vec3::from_array(positions[triangle[2] as usize]);
        let n = (b - a).cross(c - a);
        for &index in triangle {
            normals[index as usize] += n;
        }
    }
    normals
        .into_iter()
        .map(|n| n.normalize_or_zero().to_array())
        .collect()
}

fn pick(
    model: &Model,
    transforms: &[Mat4],
    visible: &[bool],
    origin: Vec3,
    direction: Vec3,
) -> Option<usize> {
    let mut best = f32::INFINITY;
    let mut selected = None;
    for mesh in &model.meshes {
        if !visible[mesh.part] {
            continue;
        }
        let inverse = transforms[mesh.part].inverse();
        let local_origin = inverse.transform_point3(origin);
        let local_direction = inverse.transform_vector3(direction);
        if !ray_box(local_origin, local_direction, mesh.min, mesh.max, best) {
            continue;
        }
        for triangle in mesh.indices.chunks_exact(3) {
            let a = Vec3::from_array(mesh.vertices[triangle[0] as usize].position);
            let b = Vec3::from_array(mesh.vertices[triangle[1] as usize].position);
            let c = Vec3::from_array(mesh.vertices[triangle[2] as usize].position);
            if let Some(distance) = ray_triangle(local_origin, local_direction, a, b, c) {
                if distance < best {
                    best = distance;
                    selected = Some(mesh.part);
                }
            }
        }
    }
    selected
}

fn ray_box(origin: Vec3, direction: Vec3, min: Vec3, max: Vec3, best: f32) -> bool {
    let inverse = Vec3::ONE / direction;
    let a = (min - origin) * inverse;
    let b = (max - origin) * inverse;
    let entry = a.min(b).max_element();
    let exit = a.max(b).min_element();
    exit >= entry.max(0.0) && entry < best
}

fn ray_triangle(origin: Vec3, direction: Vec3, a: Vec3, b: Vec3, c: Vec3) -> Option<f32> {
    let edge1 = b - a;
    let edge2 = c - a;
    let cross = direction.cross(edge2);
    let det = edge1.dot(cross);
    if det.abs() < 1e-7 {
        return None;
    }
    let inverse = 1.0 / det;
    let t = origin - a;
    let u = t.dot(cross) * inverse;
    if !(0.0..=1.0).contains(&u) {
        return None;
    }
    let q = t.cross(edge1);
    let v = direction.dot(q) * inverse;
    if v < 0.0 || u + v > 1.0 {
        return None;
    }
    let distance = edge2.dot(q) * inverse;
    (distance > 0.0).then_some(distance)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fan_motion_uses_elapsed_time_and_rpm_not_frames() {
        let a = advance_fan_angle(0.0, 1_100.0, 0.013);
        let b = advance_fan_angle(advance_fan_angle(0.0, 1_100.0, 0.005), 1_100.0, 0.008);
        assert!((a - b).abs() < 1e-12);
        assert_eq!(advance_fan_angle(0.7, 0.0, 50.0), 0.7);
        assert_eq!(advance_fan_angle(0.7, f64::NAN, 1.0), 0.7);
    }
    #[test]
    fn triangle_picking_rejects_outside_and_returns_distance() {
        let a = Vec3::new(-1.0, -1.0, 0.0);
        let b = Vec3::new(1.0, -1.0, 0.0);
        let c = Vec3::new(0.0, 1.0, 0.0);
        assert_eq!(
            ray_triangle(Vec3::new(0.0, 0.0, 4.0), -Vec3::Z, a, b, c),
            Some(4.0)
        );
        assert!(ray_triangle(Vec3::new(4.0, 0.0, 4.0), -Vec3::Z, a, b, c).is_none());
    }
    #[test]
    fn embedded_model_has_two_fans_and_no_unproved_thermal_assignment() {
        let model = load_model(
            include_bytes!("../assets/rtx3060.glb"),
            include_bytes!("../assets/rtx3060.parts.json"),
        )
        .unwrap();
        assert!(!model.meshes.is_empty());
        assert!(model.parts.iter().any(|p| p.fan_index == Some(0)));
        assert!(model.parts.iter().any(|p| p.fan_index == Some(1)));
        assert!(model
            .parts
            .iter()
            .filter_map(|p| p.thermal_binding.as_deref())
            .all(|b| b == "gpu_package" || b == "gpu"));
        assert!(model.max.x - model.min.x > 100.0);
    }
}
