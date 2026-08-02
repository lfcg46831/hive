ALTER TABLE audit.directive_export_results
    ADD COLUMN accepted_observation_version integer NULL,
    ADD COLUMN accepted_observation_content jsonb NULL,
    ADD CONSTRAINT ck_directive_export_results_accepted_observation
        CHECK (
            (accepted_observation_version IS NULL AND accepted_observation_content IS NULL)
            OR
            (accepted_observation_version = 1 AND accepted_observation_content IS NOT NULL)
        );
