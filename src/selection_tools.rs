use crate::lexicon::Candidate;
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize)]
pub enum Request {
    Add {
        word: String,
        language: usize,
    },
    Choices {
        origin: usize,
        choices: Vec<Candidate>,
    },
    Field {
        method: String,
        last_automatic_micros: Option<u64>,
        reason: Option<crate::lexicon::Decision>,
    },
}
