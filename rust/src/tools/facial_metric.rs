//! facial_metric.rs
//!
//! FaceExpressionClassification, FaceBoundingBox, and FacialMetricMatrix.

/// Classified expression from a facial metric analysis pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FaceExpressionClassification {
    Neutral,
    Happy,
    Sad,
    Surprised,
    Confused,
    Stressed,
    Angry,
    Unknown,
}

/// Axis-aligned bounding box around the detected face, in image coordinates.
#[derive(Debug, Clone, Copy)]
pub struct FaceBoundingBox {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

/// Full output of a facial analysis pass.
///
/// `landmarks` holds 68 (x, y) pairs stored as a flat array of 136 f32 values.
/// All values are normalised to [0.0, 1.0] relative to the face bounding box.
pub struct FacialMetricMatrix {
    /// 68 landmark points as flat (x0, y0, x1, y1, …, x67, y67).
    pub landmarks: [f32; 136],
    pub bounding_box: FaceBoundingBox,
    pub expression: FaceExpressionClassification,
    /// 0.0–1.0 confidence of the expression classification.
    pub confidence_score: f32,
    pub captured_at: chrono::DateTime<chrono::Utc>,
}

impl FacialMetricMatrix {
    /// Returns the (x, y) coordinate pair for landmark index `i` (0-based, 0..67).
    pub fn get_landmark(&self, i: usize) -> (f32, f32) {
        (self.landmarks[i * 2], self.landmarks[i * 2 + 1])
    }
}
