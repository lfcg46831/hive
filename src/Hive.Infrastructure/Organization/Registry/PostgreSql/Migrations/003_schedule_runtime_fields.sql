ALTER TABLE registry.schedules
    ADD COLUMN active boolean NOT NULL DEFAULT true,
    ADD COLUMN priority text NOT NULL DEFAULT 'normal',
    ADD COLUMN critical boolean NOT NULL DEFAULT false,
    ADD COLUMN catch_up text NOT NULL DEFAULT 'skip';

ALTER TABLE registry.schedules
    ALTER COLUMN active DROP DEFAULT,
    ALTER COLUMN priority DROP DEFAULT,
    ALTER COLUMN critical DROP DEFAULT,
    ALTER COLUMN catch_up DROP DEFAULT;
