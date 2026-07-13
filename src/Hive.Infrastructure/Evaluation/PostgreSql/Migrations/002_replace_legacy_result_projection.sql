DO $migration$
BEGIN
    IF to_regclass('evaluation.result_projection_dimensions') IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'evaluation'
              AND table_name = 'result_projections'
              AND column_name = 'contract_version'
        ) THEN
        DROP TABLE IF EXISTS evaluation.result_projections CASCADE;

        CREATE TABLE evaluation.result_projections (
            organization_id text NOT NULL,
            position_id text NOT NULL,
            thread_id uuid NOT NULL,
            directive_id uuid NOT NULL,
            message_id uuid NOT NULL,
            contract_version integer NOT NULL CHECK (contract_version > 0),
            rubric_version integer NOT NULL CHECK (rubric_version > 0),
            PRIMARY KEY (organization_id, thread_id, directive_id),
            UNIQUE (organization_id, message_id)
        );

        CREATE TABLE evaluation.result_projection_dimensions (
            organization_id text NOT NULL,
            thread_id uuid NOT NULL,
            directive_id uuid NOT NULL,
            dimension_id text NOT NULL CHECK (length(dimension_id) > 0),
            status text NOT NULL CHECK (status IN ('valid', 'missing', 'invalid')),
            labels text[] NOT NULL,
            PRIMARY KEY (organization_id, thread_id, directive_id, dimension_id),
            FOREIGN KEY (organization_id, thread_id, directive_id)
                REFERENCES evaluation.result_projections (organization_id, thread_id, directive_id)
                ON DELETE CASCADE
        );
    END IF;
END;
$migration$;
