CREATE TABLE github_connector.issue_correlations (
    instance_id text NOT NULL,
    organization_id text NOT NULL,
    repository text NOT NULL,
    issue_number bigint NOT NULL,
    thread_id uuid NOT NULL,
    root_directive_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (instance_id, repository, issue_number),
    UNIQUE (instance_id, organization_id, thread_id),
    UNIQUE (instance_id, organization_id, root_directive_id),
    CONSTRAINT issue_correlation_instance_nonempty CHECK (length(instance_id) > 0),
    CONSTRAINT issue_correlation_organization_nonempty CHECK (length(organization_id) > 0),
    CONSTRAINT issue_correlation_repository_nonempty CHECK (length(repository) > 0),
    CONSTRAINT issue_correlation_number_positive CHECK (issue_number > 0)
);

CREATE TABLE github_connector.issue_directive_correlations (
    instance_id text NOT NULL,
    repository text NOT NULL,
    issue_number bigint NOT NULL,
    external_event_id text NOT NULL,
    directive_id uuid NOT NULL,
    correlated_at timestamptz NOT NULL,
    PRIMARY KEY (instance_id, repository, external_event_id),
    UNIQUE (instance_id, directive_id),
    FOREIGN KEY (instance_id, repository, issue_number)
        REFERENCES github_connector.issue_correlations (instance_id, repository, issue_number),
    CONSTRAINT issue_directive_event_nonempty CHECK (length(external_event_id) > 0)
);
