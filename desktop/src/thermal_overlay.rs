//! Screen-space thermal evidence. Only the projected GPU component is spatially bound.
use eframe::egui::{self, Align2, Color32, FontId, Pos2, Rect, Shape, Stroke, StrokeKind, Vec2};

const BG: Color32 = Color32::from_rgba_unmultiplied_const(9, 18, 24, 244);
const BORDER: Color32 = Color32::from_rgb(52, 74, 80);
const TEXT: Color32 = Color32::from_rgb(226, 238, 239);
const MUTED: Color32 = Color32::from_rgb(139, 162, 170);
const GREEN: Color32 = Color32::from_rgb(161, 225, 105);

fn measured(value: Option<f32>, stale: bool) -> Option<f32> {
    value.filter(|value| !stale && value.is_finite())
}

fn temperature(value: Option<f32>) -> String {
    value.map_or_else(
        || "Sem leitura".into(),
        |v| format!("{v:.1} °C").replace('.', ","),
    )
}

#[derive(Clone, Copy)]
struct Layout {
    card: Rect,
    grid: Rect,
    cell_height: f32,
    narrow: bool,
}

fn layout(rect: Rect) -> Layout {
    let margin = 10.0_f32.min(rect.width() * 0.03).min(rect.height() * 0.04);
    let inner = rect.shrink(margin);
    let narrow = rect.width() < 440.0;
    let very_short = rect.height() < 250.0;
    let cell_height: f32 = if narrow {
        22.0
    } else if rect.height() < 400.0 {
        31.0
    } else {
        39.0
    };
    let grid_width = if narrow {
        inner.width().min(300.0)
    } else {
        inner
            .width()
            .min(228.0 + (rect.width() - 480.0).max(0.0) * 0.2)
            .min(300.0)
    };
    let grid_height: f32 = cell_height * 3.0 + if narrow { 32.0 } else { 43.0 };
    let footer = if narrow && very_short { 18.0 } else { 32.0 };
    let grid_size = Vec2::new(grid_width, grid_height.min(inner.height()));
    let grid = Rect::from_min_size(
        Pos2::new(
            inner.right() - grid_size.x,
            (inner.bottom() - footer - grid_size.y).max(inner.top()),
        ),
        grid_size,
    );
    let card_width = if very_short && !narrow {
        (grid.left() - inner.left() - 10.0).max(180.0).min(250.0)
    } else if narrow {
        inner.width().min(270.0)
    } else {
        inner.width().min(256.0)
    };
    let card_height: f32 = if narrow { 68.0 } else { 88.0 };
    let card = Rect::from_min_size(
        inner.min,
        Vec2::new(
            card_width.min(inner.width()),
            card_height.min(inner.height()),
        ),
    );
    Layout {
        card,
        grid,
        cell_height,
        narrow,
    }
}

fn chip_badge(rect: Rect, l: Layout, anchor: Pos2) -> Option<Rect> {
    let inner = rect.shrink(6.0);
    let size = egui::vec2(144.0_f32.min(inner.width()), 43.0);
    if size.x < 120.0 || inner.height() < size.y {
        return None;
    }
    for offset in [egui::vec2(-size.x / 2.0, -size.y - 8.0), -size / 2.0] {
        let requested = anchor + offset;
        let badge = Rect::from_min_size(
            Pos2::new(
                requested.x.clamp(inner.left(), inner.right() - size.x),
                requested.y.clamp(inner.top(), inner.bottom() - size.y),
            ),
            size,
        );
        if !badge.intersects(l.card.expand(4.0)) && !badge.intersects(l.grid.expand(4.0)) {
            return Some(badge);
        }
    }
    // On a crowded viewport the persistent corner card remains the readable
    // source. Never obscure it or the logical channel grid with another badge.
    None
}

/// Persistent viewport HUD, independent of the optional mechanical labels.
pub(crate) fn paint(
    painter: &egui::Painter,
    rect: Rect,
    gpu_anchor: Option<Pos2>,
    gpu_outline: Option<[Pos2; 4]>,
    data: &crate::scene::SceneTelemetry,
    thermal_mode: bool,
    covered: bool,
) {
    if !rect.is_positive() || rect.width() < 180.0 || rect.height() < 130.0 {
        return;
    }
    let painter = painter.with_clip_rect(rect.intersect(painter.clip_rect()));
    let l = layout(rect);
    let gpu = measured(data.gpu_temp, data.stale);
    let hotspot = measured(data.hotspot, data.stale);

    // This is the geometric outline of the known GPU component beneath its
    // cooler. It is not a temperature assigned to the shroud or a hotspot XY.
    if thermal_mode {
        if let (Some(value), Some(points)) = (gpu, gpu_outline) {
            if points.iter().all(|p| p.x.is_finite() && p.y.is_finite()) {
                let colour = crate::renderer::thermal_ui_colour(value);
                painter.add(Shape::convex_polygon(
                    points.to_vec(),
                    Color32::from_rgba_unmultiplied(colour.r(), colour.g(), colour.b(), 39),
                    Stroke::new(1.35_f32, colour.gamma_multiply(0.9)),
                ));
            }
        }
    }

    // Leader identifies the GPU package. There is deliberately no hotspot dot.
    if let Some(anchor) =
        gpu_anchor.filter(|p| p.x.is_finite() && p.y.is_finite() && rect.contains(*p))
    {
        let exit = Pos2::new(l.card.right(), l.card.center().y);
        let elbow = Pos2::new((exit.x + 17.0).min(rect.right() - 5.0), exit.y);
        painter.line_segment(
            [exit, elbow],
            Stroke::new(1.0_f32, GREEN.gamma_multiply(0.65)),
        );
        painter.line_segment(
            [elbow, anchor],
            Stroke::new(1.0_f32, GREEN.gamma_multiply(0.65)),
        );
    }
    painter.rect_filled(l.card, 7.0, BG);
    painter.rect_stroke(
        l.card,
        7.0,
        Stroke::new(1.0_f32, BORDER),
        StrokeKind::Inside,
    );
    painter.rect_filled(
        Rect::from_min_size(
            l.card.min + egui::vec2(0.0, 7.0),
            egui::vec2(2.5, l.card.height() - 14.0),
        ),
        1.0,
        GREEN,
    );
    let value_font = if l.narrow { 17.0 } else { 21.0 };
    let label_font = if l.narrow { 9.0 } else { 10.5 };
    let row1 = l.card.top() + if l.narrow { 14.0 } else { 21.0 };
    let row2 = row1 + if l.narrow { 22.0 } else { 27.0 };
    for (y, label, value) in [(row1, "GPU · MÉDIA", gpu), (row2, "HOTSPOT · MÁX", hotspot)] {
        painter.text(
            Pos2::new(l.card.left() + 12.0, y),
            Align2::LEFT_CENTER,
            label,
            FontId::proportional(label_font),
            MUTED,
        );
        painter.text(
            Pos2::new(l.card.right() - 11.0, y),
            Align2::RIGHT_CENTER,
            temperature(value),
            FontId::monospace(if value.is_some() {
                value_font
            } else {
                value_font - 5.0
            }),
            value
                .map(crate::renderer::thermal_ui_colour)
                .unwrap_or(MUTED),
        );
    }
    painter.text(
        Pos2::new(l.card.left() + 12.0, l.card.bottom() - 10.0),
        Align2::LEFT_CENTER,
        if covered {
            "GPU sob dissipador · máximo sem posição XY"
        } else {
            "Componente GPU · máximo sem posição XY"
        },
        FontId::proportional(if l.narrow { 7.5 } else { 8.5 }),
        MUTED,
    );

    painter.rect_filled(l.grid, 7.0, BG);
    painter.rect_stroke(
        l.grid,
        7.0,
        Stroke::new(1.0_f32, BORDER),
        StrokeKind::Inside,
    );
    let pad = 7.0;
    let title_y = l.grid.top() + 10.0;
    painter.text(
        Pos2::new(l.grid.left() + pad, title_y),
        Align2::LEFT_CENTER,
        "CANAIS TÉRMICOS",
        FontId::proportional(if l.narrow { 8.0 } else { 9.0 }),
        TEXT,
    );
    let channels_stale = data.stale || data.thermal_age_seconds.is_some_and(|age| age > 180);
    let age = if data.stale {
        "Expirado".to_owned()
    } else {
        data.thermal_age_seconds.map_or_else(
            || "Idade N/D".to_owned(),
            |s| {
                if s > 180 {
                    format!("Exp. {} min", s / 60)
                } else {
                    format!("há {s} s")
                }
            },
        )
    };
    painter.text(
        Pos2::new(l.grid.right() - pad, title_y),
        Align2::RIGHT_CENTER,
        age,
        FontId::monospace(if l.narrow { 7.5 } else { 8.5 }),
        MUTED,
    );
    let gap = 4.0;
    let cell_width = (l.grid.width() - pad * 2.0 - gap * 2.0) / 3.0;
    let start_y = l.grid.top() + if l.narrow { 20.0 } else { 24.0 };
    for index in 0..9 {
        let cell = Rect::from_min_size(
            Pos2::new(
                l.grid.left() + pad + (index % 3) as f32 * (cell_width + gap),
                start_y + (index / 3) as f32 * l.cell_height,
            ),
            Vec2::new(cell_width, l.cell_height - 3.0),
        );
        painter.rect_filled(cell, 3.0, Color32::from_rgb(18, 31, 38));
        let label = match index {
            0 => "00 · GPU média".to_owned(),
            1 => "01 · GPU máx".to_owned(),
            _ => format!("{index:02} · Desconh."),
        };
        painter.text(
            cell.min + egui::vec2(5.0, if l.narrow { 5.5 } else { 7.0 }),
            Align2::LEFT_CENTER,
            label,
            FontId::proportional(if l.narrow { 7.2 } else { 8.0 }),
            MUTED,
        );
        let value = measured(data.thermal_channels[index], channels_stale);
        let value_text =
            value.map_or_else(|| "—".into(), |v| format!("{v:.1} °C").replace('.', ","));
        painter.text(
            Pos2::new(
                cell.center().x,
                cell.bottom() - if l.narrow { 5.5 } else { 8.5 },
            ),
            Align2::CENTER_CENTER,
            value_text,
            FontId::monospace(if l.narrow { 10.5 } else { 13.0 }),
            value
                .map(crate::renderer::thermal_ui_colour)
                .unwrap_or(MUTED),
        );
    }
    painter.text(
        Pos2::new(l.grid.left() + pad, l.grid.bottom() - 7.0),
        Align2::LEFT_CENTER,
        "02–08: origem física desconhecida",
        FontId::proportional(if l.narrow { 7.0 } else { 8.0 }),
        MUTED,
    );

    if let Some(anchor) =
        gpu_anchor.filter(|p| p.x.is_finite() && p.y.is_finite() && rect.contains(*p))
    {
        if let Some(badge) = chip_badge(rect, l, anchor) {
            painter.line_segment(
                [badge.center_bottom(), anchor],
                Stroke::new(1.0_f32, GREEN.gamma_multiply(0.75)),
            );
            painter.rect_filled(badge, 5.0, BG);
            painter.rect_stroke(badge, 5.0, Stroke::new(1.0_f32, BORDER), StrokeKind::Inside);
            for (row, label, value) in [(0, "GPU", gpu), (1, "Hotspot", hotspot)] {
                let y = badge.top() + 12.0 + row as f32 * 18.0;
                painter.text(
                    Pos2::new(badge.left() + 8.0, y),
                    Align2::LEFT_CENTER,
                    label,
                    FontId::proportional(10.0),
                    MUTED,
                );
                let value_text = value.map_or_else(
                    || "—".to_owned(),
                    |v| format!("{v:.1} °C").replace('.', ","),
                );
                painter.text(
                    Pos2::new(badge.right() - 8.0, y),
                    Align2::RIGHT_CENTER,
                    value_text,
                    FontId::monospace(12.5),
                    value
                        .map(crate::renderer::thermal_ui_colour)
                        .unwrap_or(MUTED),
                );
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unavailable_and_non_finite_values_never_become_numbers() {
        assert_eq!(measured(Some(0.0), false), Some(0.0));
        assert_eq!(measured(Some(42.0), true), None);
        assert_eq!(measured(Some(f32::NAN), false), None);
        assert_eq!(measured(Some(f32::INFINITY), false), None);
        assert_eq!(temperature(None), "Sem leitura");
    }

    #[test]
    fn hud_stays_inside_small_viewports() {
        for size in [
            egui::vec2(480.0, 190.0),
            egui::vec2(480.0, 300.0),
            egui::vec2(480.0, 600.0),
            egui::vec2(320.0, 240.0),
        ] {
            let rect = Rect::from_min_size(Pos2::new(39.0, 71.0), size);
            let l = layout(rect);
            assert!(rect.contains_rect(l.card));
            assert!(rect.contains_rect(l.grid));
            assert!(!l.card.intersects(l.grid), "card/grid overlap at {size:?}");
        }
    }

    #[test]
    fn chip_badge_uses_only_gpu_anchor_and_avoids_other_hud_cards() {
        let rect = Rect::from_min_size(Pos2::ZERO, egui::vec2(900.0, 600.0));
        let l = layout(rect);
        let badge = chip_badge(rect, l, Pos2::new(450.0, 275.0)).expect("room near GPU");
        assert!(rect.contains_rect(badge));
        assert!(!badge.intersects(l.card));
        assert!(!badge.intersects(l.grid));
        assert!((badge.center().x - 450.0).abs() < 1.0);
        assert!(chip_badge(rect, l, l.card.center()).is_none());
    }

    #[test]
    fn hud_tessellates_without_gpu_or_mechanical_label_state() {
        let ctx = egui::Context::default();
        let output = ctx.run(egui::RawInput::default(), |ctx| {
            let painter = ctx.layer_painter(egui::LayerId::new(
                egui::Order::Foreground,
                egui::Id::new("thermal-hud-test"),
            ));
            let rect = Rect::from_min_size(Pos2::ZERO, egui::vec2(480.0, 320.0));
            let data = crate::scene::SceneTelemetry {
                gpu_temp: Some(36.0),
                hotspot: Some(46.0),
                thermal_channels: [Some(36.0); 9],
                thermal_age_seconds: Some(3),
                ..Default::default()
            };
            paint(
                &painter,
                rect,
                Some(Pos2::new(250.0, 140.0)),
                Some([
                    Pos2::new(220.0, 125.0),
                    Pos2::new(270.0, 125.0),
                    Pos2::new(270.0, 155.0),
                    Pos2::new(220.0, 155.0),
                ]),
                &data,
                true,
                true,
            );
        });
        assert!(!output.shapes.is_empty());
        let primitives = ctx.tessellate(output.shapes, output.pixels_per_point);
        assert!(!primitives.is_empty());
        for primitive in primitives {
            if let egui::epaint::Primitive::Mesh(mesh) = primitive.primitive {
                assert!(mesh
                    .vertices
                    .iter()
                    .all(|v| v.pos.x.is_finite() && v.pos.y.is_finite()));
            }
        }
    }
}
