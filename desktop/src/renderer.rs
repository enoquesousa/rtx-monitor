//! Depth-tested native 3D rendering into an off-screen WGPU target, composed by egui.
use std::{io::BufWriter, path::Path, sync::Arc, time::Duration};

use bytemuck::{Pod, Zeroable};
use eframe::{
    egui,
    egui_wgpu::{self, wgpu},
};
use glam::{Mat4, Vec3};
use wgpu::util::DeviceExt;

use crate::scene::{load_model, Model, SceneState};

pub const THERMAL_MIN_C: f32 = 20.0;
pub const THERMAL_MAX_C: f32 = 95.0;
pub const THERMAL_ANCHORS_C: [f32; 4] = [20.0, 46.25, 72.5, 95.0];
const THERMAL_PALETTE_LINEAR: [[f32; 3]; 4] = [
    [0.055, 0.32, 0.76],
    [0.04, 0.72, 0.40],
    [1.0, 0.42, 0.035],
    [0.95, 0.055, 0.025],
];
// Rgba8UnormSrgb and Depth32Float guarantee 4x multisampling in WebGPU's
// baseline format capabilities. egui's compositor remains single-sampled.
const SCENE_SAMPLES: u32 = 4;
const EXPOSURE_SAMPLES: usize = 8;

/// The unlit thermal ramp shown by the legend. Model lighting can shade this
/// base colour, but the temperature stops and sRGB encoding match the shader.
pub fn thermal_ui_colour(temperature: f32) -> egui::Color32 {
    if !temperature.is_finite() {
        return egui::Color32::from_rgb(128, 138, 145);
    }
    let t = ((temperature - THERMAL_MIN_C) / (THERMAL_MAX_C - THERMAL_MIN_C)).clamp(0.0, 1.0);
    let (from, to, mix) = if t < 0.35 {
        (0, 1, t / 0.35)
    } else if t < 0.70 {
        (1, 2, (t - 0.35) / 0.35)
    } else {
        (2, 3, (t - 0.70) / 0.30)
    };
    let linear = [0, 1, 2].map(|channel| {
        THERMAL_PALETTE_LINEAR[from][channel]
            + (THERMAL_PALETTE_LINEAR[to][channel] - THERMAL_PALETTE_LINEAR[from][channel]) * mix
    });
    let gamma = linear.map(|value| {
        let encoded = if value <= 0.0031308 {
            value * 12.92
        } else {
            1.055 * value.powf(1.0 / 2.4) - 0.055
        };
        (encoded * 255.0).round().clamp(0.0, 255.0) as u8
    });
    egui::Color32::from_rgb(gamma[0], gamma[1], gamma[2])
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub(crate) struct Vertex {
    pub position: [f32; 3],
    pub normal: [f32; 3],
    pub uv: [f32; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Instance {
    model: [[f32; 4]; 4],
    colour: [f32; 4],
    material: [f32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Globals {
    view_projection: [[f32; 4]; 4],
    eye: [f32; 4],
    thermal: [f32; 4],
    ambient: [f32; 4],
}

pub(crate) struct FrameData {
    pub size_points: [f32; 2],
    pub view_projection: [[f32; 4]; 4],
    pub eye: [f32; 3],
    pub transforms: Vec<Mat4>,
    pub visible: Vec<bool>,
    pub selected: Option<usize>,
    pub gpu_temperature: Option<f32>,
    pub thermal_mode: bool,
    /// Measured RPM converted to radians/second; None also pauses visual exposure.
    pub fan_angular_velocity: [Option<f32>; 2],
    /// Camera-like exposure duration. This is a visual parameter, not a sensor.
    pub exposure_seconds: f32,
}

pub(crate) struct SceneCallback {
    pub frame: Arc<FrameData>,
}

impl egui_wgpu::CallbackTrait for SceneCallback {
    fn prepare(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        screen: &egui_wgpu::ScreenDescriptor,
        encoder: &mut wgpu::CommandEncoder,
        resources: &mut egui_wgpu::CallbackResources,
    ) -> Vec<wgpu::CommandBuffer> {
        if let Some(renderer) = resources.get_mut::<RenderResources>() {
            renderer.prepare(device, queue, encoder, screen.pixels_per_point, &self.frame);
        }
        Vec::new()
    }
    fn paint(
        &self,
        _info: egui::PaintCallbackInfo,
        pass: &mut wgpu::RenderPass<'static>,
        resources: &egui_wgpu::CallbackResources,
    ) {
        if let Some(renderer) = resources.get::<RenderResources>() {
            renderer.paint(pass);
        }
    }
}

struct Mesh {
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    index_count: u32,
    part: usize,
    colour: [f32; 4],
    roughness: f32,
    metallic: f32,
    thermal: bool,
    fan_index: Option<usize>,
    fan_pivot: Vec3,
}
struct Target {
    _colour: wgpu::Texture,
    view: wgpu::TextureView,
    _multisample_colour: wgpu::Texture,
    multisample_view: wgpu::TextureView,
    _depth: wgpu::Texture,
    depth: wgpu::TextureView,
    bind_group: wgpu::BindGroup,
    _accumulation: wgpu::Texture,
    accumulation_view: wgpu::TextureView,
    accumulation_group: wgpu::BindGroup,
    size: [u32; 2],
}

pub(crate) struct RenderResources {
    mesh_pipeline: wgpu::RenderPipeline,
    background_pipeline: wgpu::RenderPipeline,
    blit_pipeline: wgpu::RenderPipeline,
    accumulation_pipeline: wgpu::RenderPipeline,
    exposure_resolve_pipeline: wgpu::RenderPipeline,
    uniform: wgpu::Buffer,
    uniform_group: wgpu::BindGroup,
    instance_buffer: wgpu::Buffer,
    texture_layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    meshes: Vec<Mesh>,
    target: Option<Target>,
}

impl RenderResources {
    pub fn new(state: &egui_wgpu::RenderState, model: &Model) -> Self {
        Self::new_for_target(&state.device, state.target_format, model)
    }

    fn new_for_target(
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
        model: &Model,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("RTX model physical materials"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/model.wgsl").into()),
        });
        let uniform_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("RTX model camera"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        let uniform = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("RTX model camera uniforms"),
            contents: bytemuck::bytes_of(&Globals::zeroed()),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let uniform_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("RTX model camera bind group"),
            layout: &uniform_layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform.as_entire_binding(),
            }],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("RTX model pipeline"),
            bind_group_layouts: &[&uniform_layout],
            push_constant_ranges: &[],
        });
        let vertices = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<Vertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &wgpu::vertex_attr_array![0 => Float32x3, 1 => Float32x3, 2 => Float32x2],
        };
        let instances = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<Instance>() as u64,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &wgpu::vertex_attr_array![3 => Float32x4, 4 => Float32x4, 5 => Float32x4, 6 => Float32x4, 7 => Float32x4, 8 => Float32x4],
        };
        let mesh_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("RTX reference mesh shading"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("mesh_vs"),
                buffers: &[vertices, instances],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("mesh_fs"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::Rgba8UnormSrgb,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: wgpu::TextureFormat::Depth32Float,
                depth_write_enabled: true,
                depth_compare: wgpu::CompareFunction::LessEqual,
                stencil: Default::default(),
                bias: Default::default(),
            }),
            multisample: wgpu::MultisampleState {
                count: SCENE_SAMPLES,
                ..Default::default()
            },
            multiview: None,
            cache: None,
        });
        let background_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("RTX studio backdrop"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("background_vs"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("background_fs"),
                targets: &[Some(wgpu::TextureFormat::Rgba8UnormSrgb.into())],
                compilation_options: Default::default(),
            }),
            primitive: Default::default(),
            depth_stencil: Some(wgpu::DepthStencilState {
                format: wgpu::TextureFormat::Depth32Float,
                depth_write_enabled: false,
                depth_compare: wgpu::CompareFunction::Always,
                stencil: Default::default(),
                bias: Default::default(),
            }),
            multisample: wgpu::MultisampleState {
                count: SCENE_SAMPLES,
                ..Default::default()
            },
            multiview: None,
            cache: None,
        });
        let texture_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("RTX scene colour target"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let blit_shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("RTX scene egui compositor"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/composite.wgsl").into()),
        });
        let blit_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("RTX compositor layout"),
            bind_group_layouts: &[&texture_layout],
            push_constant_ranges: &[],
        });
        let blit_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("RTX native scene compositor"),
            layout: Some(&blit_layout),
            vertex: wgpu::VertexState {
                module: &blit_shader,
                entry_point: Some("vs_main"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &blit_shader,
                entry_point: Some(if target_format.is_srgb() {
                    "fs_linear"
                } else {
                    "fs_main"
                }),
                targets: &[Some(target_format.into())],
                compilation_options: Default::default(),
            }),
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            multiview: None,
            cache: None,
        });
        let composite_pipeline = |label, format, blend| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&blit_layout),
                vertex: wgpu::VertexState {
                    module: &blit_shader,
                    entry_point: Some("vs_main"),
                    buffers: &[],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &blit_shader,
                    entry_point: Some("fs_linear"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive: Default::default(),
                depth_stencil: None,
                multisample: Default::default(),
                multiview: None,
                cache: None,
            })
        };
        let average_blend = wgpu::BlendComponent {
            src_factor: wgpu::BlendFactor::Constant,
            dst_factor: wgpu::BlendFactor::One,
            operation: wgpu::BlendOperation::Add,
        };
        let accumulation_pipeline = composite_pipeline(
            "RTX linear shutter accumulation",
            wgpu::TextureFormat::Rgba16Float,
            Some(wgpu::BlendState {
                color: average_blend,
                alpha: average_blend,
            }),
        );
        let exposure_resolve_pipeline = composite_pipeline(
            "RTX linear exposure to sRGB",
            wgpu::TextureFormat::Rgba8UnormSrgb,
            None,
        );
        let meshes: Vec<_> = model
            .meshes
            .iter()
            .map(|mesh| Mesh {
                vertices: device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("RTX part vertices"),
                    contents: bytemuck::cast_slice(&mesh.vertices),
                    usage: wgpu::BufferUsages::VERTEX,
                }),
                indices: device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("RTX part indices"),
                    contents: bytemuck::cast_slice(&mesh.indices),
                    usage: wgpu::BufferUsages::INDEX,
                }),
                index_count: mesh.indices.len() as u32,
                part: mesh.part,
                colour: mesh.colour,
                roughness: mesh.roughness,
                metallic: mesh.metallic,
                thermal: matches!(
                    model.parts[mesh.part].thermal_binding.as_deref(),
                    Some("gpu_package" | "gpu")
                ),
                fan_index: model.parts[mesh.part].fan_index.filter(|&index| index < 2),
                fan_pivot: Vec3::from_array(model.parts[mesh.part].pivot),
            })
            .collect();
        let instance_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("RTX part pose stream"),
            size: (EXPOSURE_SAMPLES * meshes.len().max(1) * std::mem::size_of::<Instance>()) as u64,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("RTX scene sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        Self {
            mesh_pipeline,
            background_pipeline,
            blit_pipeline,
            accumulation_pipeline,
            exposure_resolve_pipeline,
            uniform,
            uniform_group,
            instance_buffer,
            texture_layout,
            sampler,
            meshes,
            target: None,
        }
    }

    fn resize(&mut self, device: &wgpu::Device, size: [u32; 2]) {
        if self
            .target
            .as_ref()
            .is_some_and(|target| target.size == size)
        {
            return;
        }
        let extent = wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        };
        let colour = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("RTX model render colour"),
            size: extent,
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = colour.create_view(&Default::default());
        let multisample_colour = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("RTX model 4x antialias colour"),
            size: extent,
            mip_level_count: 1,
            sample_count: SCENE_SAMPLES,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let multisample_view = multisample_colour.create_view(&Default::default());
        let depth = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("RTX model depth"),
            size: extent,
            mip_level_count: 1,
            sample_count: SCENE_SAMPLES,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Depth32Float,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let depth_view = depth.create_view(&Default::default());
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("RTX scene colour binding"),
            layout: &self.texture_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        });
        let accumulation = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("RTX linear temporal exposure"),
            size: extent,
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba16Float,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let accumulation_view = accumulation.create_view(&Default::default());
        let accumulation_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("RTX exposure binding"),
            layout: &self.texture_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&accumulation_view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        });
        self.target = Some(Target {
            _colour: colour,
            view,
            _multisample_colour: multisample_colour,
            multisample_view,
            _depth: depth,
            depth: depth_view,
            bind_group,
            _accumulation: accumulation,
            accumulation_view,
            accumulation_group,
            size,
        });
    }

    fn prepare(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        pixels_per_point: f32,
        frame: &FrameData,
    ) {
        let size = [
            (frame.size_points[0] * pixels_per_point)
                .round()
                .clamp(1.0, 4096.0) as u32,
            (frame.size_points[1] * pixels_per_point)
                .round()
                .clamp(1.0, 4096.0) as u32,
        ];
        self.resize(device, size);
        let globals = Globals {
            view_projection: frame.view_projection,
            eye: [frame.eye[0], frame.eye[1], frame.eye[2], 1.0],
            thermal: [
                frame.gpu_temperature.unwrap_or(0.0),
                THERMAL_MIN_C,
                THERMAL_MAX_C,
                if frame.thermal_mode && frame.gpu_temperature.is_some() {
                    1.0
                } else {
                    0.0
                },
            ],
            ambient: [0.22, 0.25, 0.27, 1.0],
        };
        queue.write_buffer(&self.uniform, 0, bytemuck::bytes_of(&globals));
        let exposure_samples = if self
            .meshes
            .iter()
            .any(|mesh| mesh.fan_index.is_some() && frame.visible[mesh.part])
        {
            exposure_sample_count(frame)
        } else {
            1
        };
        let instances: Vec<_> = (0..exposure_samples)
            .flat_map(|sample| {
                self.meshes.iter().map(move |mesh| Instance {
                    model: exposure_transform(
                        frame.transforms[mesh.part],
                        mesh.fan_pivot,
                        mesh.fan_index
                            .and_then(|index| frame.fan_angular_velocity[index]),
                        frame.exposure_seconds,
                        sample,
                        exposure_samples,
                    )
                    .to_cols_array_2d(),
                    colour: mesh.colour,
                    material: [
                        mesh.roughness,
                        mesh.metallic,
                        if mesh.thermal { 1.0 } else { 0.0 },
                        if frame.selected == Some(mesh.part) {
                            1.0
                        } else {
                            0.0
                        },
                    ],
                })
            })
            .collect();
        queue.write_buffer(&self.instance_buffer, 0, bytemuck::cast_slice(&instances));
        let target = self.target.as_ref().unwrap();
        for sample in 0..exposure_samples {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("RTX native model depth render"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &target.multisample_view,
                    resolve_target: Some(&target.view),
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: 0.012,
                            g: 0.02,
                            b: 0.025,
                            a: 1.0,
                        }),
                        store: wgpu::StoreOp::Discard,
                    },
                    depth_slice: None,
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &target.depth,
                    depth_ops: Some(wgpu::Operations {
                        load: wgpu::LoadOp::Clear(1.0),
                        store: wgpu::StoreOp::Discard,
                    }),
                    stencil_ops: None,
                }),
                timestamp_writes: None,
                occlusion_query_set: None,
            });
            pass.set_bind_group(0, &self.uniform_group, &[]);
            pass.set_pipeline(&self.background_pipeline);
            pass.draw(0..3, 0..1);
            pass.set_pipeline(&self.mesh_pipeline);
            for (index, mesh) in self.meshes.iter().enumerate() {
                if !frame.visible[mesh.part] {
                    continue;
                }
                pass.set_vertex_buffer(0, mesh.vertices.slice(..));
                let start = (sample * self.meshes.len() + index) as u64
                    * std::mem::size_of::<Instance>() as u64;
                pass.set_vertex_buffer(
                    1,
                    self.instance_buffer
                        .slice(start..start + std::mem::size_of::<Instance>() as u64),
                );
                pass.set_index_buffer(mesh.indices.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..mesh.index_count, 0, 0..1);
            }
            drop(pass);
            if exposure_samples > 1 {
                let mut accumulate = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("RTX integrate shutter sample in linear light"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: &target.accumulation_view,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: if sample == 0 {
                                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT)
                            } else {
                                wgpu::LoadOp::Load
                            },
                            store: wgpu::StoreOp::Store,
                        },
                        depth_slice: None,
                    })],
                    depth_stencil_attachment: None,
                    timestamp_writes: None,
                    occlusion_query_set: None,
                });
                accumulate.set_pipeline(&self.accumulation_pipeline);
                accumulate.set_bind_group(0, &target.bind_group, &[]);
                let weight = 1.0 / exposure_samples as f64;
                accumulate.set_blend_constant(wgpu::Color {
                    r: weight,
                    g: weight,
                    b: weight,
                    a: weight,
                });
                accumulate.draw(0..3, 0..1);
            }
        }
        if exposure_samples > 1 {
            let mut resolve = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("RTX resolve shutter exposure to scene colour"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &target.view,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                        store: wgpu::StoreOp::Store,
                    },
                    depth_slice: None,
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
            });
            resolve.set_pipeline(&self.exposure_resolve_pipeline);
            resolve.set_bind_group(0, &target.accumulation_group, &[]);
            resolve.draw(0..3, 0..1);
        }
    }

    fn paint(&self, pass: &mut wgpu::RenderPass<'_>) {
        if let Some(target) = &self.target {
            pass.set_pipeline(&self.blit_pipeline);
            pass.set_bind_group(0, &target.bind_group, &[]);
            pass.draw(0..3, 0..1);
        }
    }
}

fn exposure_sample_count(frame: &FrameData) -> usize {
    let duration = valid_exposure(frame.exposure_seconds);
    if frame
        .fan_angular_velocity
        .iter()
        .flatten()
        .any(|velocity| velocity.is_finite() && velocity.abs() * duration > 0.025)
    {
        EXPOSURE_SAMPLES
    } else {
        1
    }
}

fn valid_exposure(seconds: f32) -> f32 {
    if seconds.is_finite() {
        seconds.clamp(0.0, 1.0 / 30.0)
    } else {
        0.0
    }
}

fn exposure_transform(
    pose: Mat4,
    pivot: Vec3,
    angular_velocity: Option<f32>,
    seconds: f32,
    sample: usize,
    count: usize,
) -> Mat4 {
    if count <= 1 {
        return pose;
    }
    let Some(velocity) = angular_velocity.filter(|velocity| velocity.is_finite()) else {
        return pose;
    };
    let duration = valid_exposure(seconds);
    if velocity == 0.0 || duration == 0.0 {
        return pose;
    }
    let seconds_before_present = duration * (sample as f32 + 0.5) / count as f32;
    pose * Mat4::from_translation(pivot)
        * Mat4::from_rotation_y(-velocity * seconds_before_present)
        * Mat4::from_translation(-pivot)
}

/// Export the actual native scene pipeline without starting acquisition or a window.
/// No telemetry is supplied: rotors retain a fixed inspection phase and no measured
/// temperature is assigned to any mesh. This uses the same buffers, shader, camera
/// and off-screen target as `SceneCallback`, before egui adds its text overlays.
pub fn render_preview(path: &Path, exploded: bool) -> Result<serde_json::Value, String> {
    render_preview_sequence(path, exploded, None)
}

/// A reproducible optical-aliasing fixture, never a claimed hardware capture.
/// Exports 24 instantaneous and 24 shutter-integrated native frames at 55 Hz.
pub fn render_motion_preview(path: &Path, rpm: f32) -> Result<serde_json::Value, String> {
    if !rpm.is_finite() || !(0.0..=30_000.0).contains(&rpm) {
        return Err("RPM da demonstração fora do intervalo 0–30000".into());
    }
    std::fs::create_dir_all(path).map_err(|error| format!("Pasta de demonstração: {error}"))?;
    render_preview_sequence(path, false, Some(rpm))
}

fn render_preview_sequence(
    path: &Path,
    exploded: bool,
    motion_rpm: Option<f32>,
) -> Result<serde_json::Value, String> {
    let model = Arc::new(load_model(
        include_bytes!("../assets/rtx3060.glb"),
        include_bytes!("../assets/rtx3060.parts.json"),
    )?);
    let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
    let options = |fallback| wgpu::RequestAdapterOptions {
        power_preference: wgpu::PowerPreference::LowPower,
        force_fallback_adapter: fallback,
        compatible_surface: None,
    };
    let (adapter, used_fallback_request) =
        match pollster::block_on(instance.request_adapter(&options(true))) {
            Ok(adapter) => (adapter, true),
            Err(_) => (
                pollster::block_on(instance.request_adapter(&options(false)))
                    .map_err(|error| format!("Adaptador WGPU para preview: {error}"))?,
                false,
            ),
        };
    let info = adapter.get_info();
    let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
        label: Some("RTX native preview without telemetry"),
        ..Default::default()
    }))
    .map_err(|error| format!("Dispositivo WGPU para preview: {error}"))?;
    device.push_error_scope(wgpu::ErrorFilter::Validation);
    let mut renderer =
        RenderResources::new_for_target(&device, wgpu::TextureFormat::Rgba8UnormSrgb, &model);
    let [width, height] = if motion_rpm.is_some() {
        [960_u32, 640_u32]
    } else {
        [1440_u32, 960_u32]
    };
    let frame_count = if motion_rpm.is_some() { 48 } else { 1 };
    let mut frame_reports = Vec::with_capacity(frame_count);
    for frame_number in 0..frame_count {
        if frame_number > 0 {
            device.push_error_scope(wgpu::ErrorFilter::Validation);
        }
        let mut frame =
            SceneState::preview_frame(Arc::clone(&model), [width as f32, height as f32], exploded);
        let integrated = motion_rpm.is_some() && frame_number >= 24;
        let index = frame_number % 24;
        let output = if motion_rpm.is_some() {
            path.join(format!(
                "{}-{index:03}.png",
                if integrated { "exposure" } else { "instant" }
            ))
        } else {
            path.to_owned()
        };
        if let Some(rpm) = motion_rpm {
            let angular_velocity = rpm * std::f32::consts::TAU / 60.0;
            let angle = angular_velocity * index as f32 / 55.0;
            for (part_index, part) in model.parts.iter().enumerate() {
                if part.fan_index.is_some() {
                    let pivot = Vec3::from_array(part.pivot);
                    frame.transforms[part_index] = Mat4::from_translation(pivot)
                        * Mat4::from_rotation_y(angle)
                        * Mat4::from_translation(-pivot);
                }
            }
            frame.fan_angular_velocity = [Some(angular_velocity); 2];
            frame.exposure_seconds = if integrated { 1.0 / 55.0 } else { 0.0 };
        }
        let started = std::time::Instant::now();
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("RTX native preview render and readback"),
        });
        renderer.prepare(&device, &queue, &mut encoder, 1.0, &frame);
        let row_bytes = width * 4;
        let aligned_row_bytes = row_bytes.div_ceil(wgpu::COPY_BYTES_PER_ROW_ALIGNMENT)
            * wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("RTX native preview pixel readback"),
            size: u64::from(aligned_row_bytes) * u64::from(height),
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let target = renderer
            .target
            .as_ref()
            .ok_or("Preview WGPU sem render target")?;
        encoder.copy_texture_to_buffer(
            wgpu::TexelCopyTextureInfo {
                texture: &target._colour,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyBufferInfo {
                buffer: &readback,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(aligned_row_bytes),
                    rows_per_image: Some(height),
                },
            },
            wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
        );
        let submission = queue.submit(Some(encoder.finish()));
        let slice = readback.slice(..);
        let (sender, receiver) = std::sync::mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |result| {
            let _ = sender.send(result);
        });
        device
            .poll(wgpu::PollType::Wait {
                submission_index: Some(submission),
                timeout: Some(Duration::from_secs(30)),
            })
            .map_err(|error| format!("Conclusão do preview WGPU: {error}"))?;
        receiver
            .recv_timeout(Duration::from_secs(5))
            .map_err(|error| format!("Leitura do preview WGPU: {error}"))?
            .map_err(|error| format!("Mapeamento do preview WGPU: {error}"))?;
        if let Some(error) = pollster::block_on(device.pop_error_scope()) {
            return Err(format!("Validação do preview WGPU: {error}"));
        }
        let render_readback_ms = started.elapsed().as_secs_f64() * 1000.0;
        let mapped = slice.get_mapped_range();
        let mut pixels = Vec::with_capacity((row_bytes * height) as usize);
        for row in mapped.chunks_exact(aligned_row_bytes as usize) {
            pixels.extend_from_slice(&row[..row_bytes as usize]);
        }
        drop(mapped);
        readback.unmap();
        let file =
            std::fs::File::create(&output).map_err(|error| format!("Arquivo PNG: {error}"))?;
        let mut encoder = png::Encoder::new(BufWriter::new(file), width, height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder
            .write_header()
            .map_err(|error| format!("Cabeçalho PNG: {error}"))?;
        writer
            .write_image_data(&pixels)
            .map_err(|error| format!("Pixels PNG: {error}"))?;
        writer
            .finish()
            .map_err(|error| format!("Conclusão PNG: {error}"))?;
        frame_reports.push(serde_json::json!({ "path": output.display().to_string(), "index": index, "shutter_integrated": integrated, "exposure_samples": exposure_sample_count(&frame), "render_and_readback_ms": render_readback_ms }));
    }
    let report = serde_json::json!({
        "renderer": "native_wgpu_same_as_app",
        "adapter": info.name,
        "backend": format!("{:?}", info.backend),
        "device_type": format!("{:?}", info.device_type),
        "fallback_adapter_requested": used_fallback_request,
        "software_adapter": info.device_type == wgpu::DeviceType::Cpu,
        "width": width, "height": height, "path": path.display().to_string(),
        "msaa_samples": SCENE_SAMPLES,
        "parts": model.parts.len(), "primitives": model.meshes.len(),
        "triangles": model.meshes.iter().map(|mesh| mesh.indices.len() / 3).sum::<usize>(),
        "exploded": exploded, "egui_overlays_included": false,
        "telemetry_queried": false, "measured_temperatures_assigned": false,
        "fan_phase": if motion_rpm.is_some() { "synthetic constant RPM fixture; no RPM sampled" } else { "fixed inspection phase; no RPM sampled" },
        "synthetic_rpm": motion_rpm, "fixture_fps": if motion_rpm.is_some() { Some(55) } else { None },
        "fixture_shutter_seconds": if motion_rpm.is_some() { Some(1.0 / 55.0) } else { None },
        "fixture_rotation_degrees_per_frame": motion_rpm.map(|rpm| rpm * 6.0 / 55.0),
        "fixture_blade_passes_per_frame": motion_rpm.map(|rpm| rpm * 9.0 / (60.0 * 55.0)),
        "integration": "box shutter; eight midpoint samples; normalized equal weights in linear light; independent depth/MSAA per sample",
        "rotor_nodes": model.parts.iter().filter(|part| part.fan_index.is_some()).map(|part| &part.node).collect::<Vec<_>>(),
        "frames": frame_reports,
    });
    if motion_rpm.is_some() {
        std::fs::write(
            path.join("manifest.json"),
            serde_json::to_vec_pretty(&report).map_err(|error| error.to_string())?,
        )
        .map_err(|error| error.to_string())?;
    }
    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::{
        exposure_transform, thermal_ui_colour, wgpu, Mat4, Vec3, EXPOSURE_SAMPLES,
        THERMAL_ANCHORS_C,
    };

    #[test]
    fn shutter_rotates_blade_points_and_hub_together_about_the_exploded_pivot() {
        let pivot = Vec3::new(98.0, 29.5, 0.0);
        let explosion = Vec3::new(0.0, 75.0, 0.0);
        let current_angle = 0.7_f32;
        let angular_velocity = 1100.0 * std::f32::consts::TAU / 60.0;
        let shutter = 1.0 / 55.0;
        let pose = Mat4::from_translation(explosion + pivot)
            * Mat4::from_rotation_y(current_angle)
            * Mat4::from_translation(-pivot);
        for sample in 0..EXPOSURE_SAMPLES {
            let exposed = exposure_transform(
                pose,
                pivot,
                Some(angular_velocity),
                shutter,
                sample,
                EXPOSURE_SAMPLES,
            );
            assert!(exposed.transform_point3(pivot).distance(pivot + explosion) < 0.0001);
            let theta = current_angle
                - angular_velocity * shutter * (sample as f32 + 0.5) / EXPOSURE_SAMPLES as f32;
            let expected =
                pivot + explosion + Vec3::new(40.0 * theta.cos(), 1.0, -40.0 * theta.sin());
            assert!(
                exposed
                    .transform_point3(pivot + Vec3::new(40.0, 1.0, 0.0))
                    .distance(expected)
                    < 0.0001
            );
        }
        assert_eq!(
            exposure_transform(pose, pivot, None, shutter, 0, EXPOSURE_SAMPLES),
            pose
        );
        assert_eq!(
            exposure_transform(
                pose,
                pivot,
                Some(angular_velocity),
                0.0,
                0,
                EXPOSURE_SAMPLES
            ),
            pose
        );
    }

    #[test]
    fn native_render_shaders_parse_and_validate_without_a_gpu() {
        for (name, source) in [
            ("model", include_str!("shaders/model.wgsl")),
            ("composite", include_str!("shaders/composite.wgsl")),
        ] {
            let module = wgpu::naga::front::wgsl::parse_str(source)
                .unwrap_or_else(|error| panic!("{name}: {}", error.emit_to_string(source)));
            wgpu::naga::valid::Validator::new(
                wgpu::naga::valid::ValidationFlags::all(),
                wgpu::naga::valid::Capabilities::all(),
            )
            .validate(&module)
            .unwrap_or_else(|error| panic!("{name}: {error:?}"));
        }
    }

    #[test]
    fn thermal_legend_uses_the_shader_stops_and_linear_to_srgb_encoding() {
        let shader = include_str!("shaders/model.wgsl");
        for exact_anchor in [
            "vec3<f32>(0.055, 0.32, 0.76)",
            "vec3<f32>(0.04, 0.72, 0.40)",
            "vec3<f32>(1.0, 0.42, 0.035)",
            "vec3<f32>(0.95, 0.055, 0.025)",
            "t < 0.35",
            "t < 0.70",
            "(t - 0.70) / 0.30",
        ] {
            assert!(
                shader.contains(exact_anchor),
                "Thermal shader ramp changed without its legend: {exact_anchor}"
            );
        }
        assert_eq!(THERMAL_ANCHORS_C, [20.0, 46.25, 72.5, 95.0]);
        let expected = [
            [66, 153, 226],
            [56, 221, 170],
            [255, 173, 53],
            [249, 66, 44],
        ];
        for (temperature, rgb) in THERMAL_ANCHORS_C.into_iter().zip(expected) {
            let actual = thermal_ui_colour(temperature);
            for (found, reference) in [actual.r(), actual.g(), actual.b()].into_iter().zip(rgb) {
                assert!(
                    found.abs_diff(reference) <= 1,
                    "Incorrect sRGB ramp colour at {temperature} C"
                );
            }
        }
        assert_eq!(thermal_ui_colour(-100.0), thermal_ui_colour(20.0));
        assert_eq!(thermal_ui_colour(200.0), thermal_ui_colour(95.0));
        assert_ne!(thermal_ui_colour(f32::NAN), thermal_ui_colour(20.0));
    }
}
