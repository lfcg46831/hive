-- US-F0-13-T12c: closed-vocabulary diagnostic code for missing/invalid envelope
-- projections. Additive and idempotent; valid dimensions keep a NULL code and the
-- column never stores model text or rejected values.
ALTER TABLE evaluation.result_projection_dimensions
    ADD COLUMN IF NOT EXISTS diagnostic_code text
        CHECK (diagnostic_code IS NULL OR length(diagnostic_code) > 0);
