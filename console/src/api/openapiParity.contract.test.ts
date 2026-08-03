import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { PUBLIC_API_ROUTE_TEMPLATES } from './client.js';
import { enumWireShape, objectWireShape } from './wireShape.js';

interface OpenApiSchema {
  properties?: Record<string, unknown>;
  enum?: string[];
}

interface OpenApiDocument {
  paths: Record<string, unknown>;
  components: { schemas: Record<string, OpenApiSchema> };
}

const documentPath =
  process.env.HIVE_OPENAPI_DOCUMENT ??
  fileURLToPath(new URL('../../openapi/v1.json', import.meta.url));

if (!existsSync(documentPath)) {
  throw new Error(
    `The published OpenAPI document was not found at ${documentPath}. ` +
      'Export it first with: dotnet test tests/Hive.Tests --filter PublicApiOpenApiSnapshotTests',
  );
}

const document = JSON.parse(readFileSync(documentPath, 'utf8')) as OpenApiDocument;

describe('public API parity', () => {
  it('calls exactly the documented public routes', () => {
    expect([...PUBLIC_API_ROUTE_TEMPLATES].sort()).toEqual(
      Object.keys(document.paths).sort(),
    );
  });

  it('never targets the private internal surface', () => {
    expect(Object.keys(document.paths).some((path) => path.startsWith('/internal'))).toBe(
      false,
    );
  });

  it.each(Object.entries(objectWireShape))(
    '%s mirrors the documented properties',
    (schemaName, properties) => {
      const schema = document.components.schemas[schemaName];
      expect(schema, `${schemaName} is missing from the document`).toBeDefined();
      expect([...properties].sort()).toEqual(
        Object.keys(schema?.properties ?? {}).sort(),
      );
    },
  );

  it.each(Object.entries(enumWireShape))(
    '%s mirrors the documented values in order',
    (schemaName, values) => {
      const schema = document.components.schemas[schemaName];
      expect(schema, `${schemaName} is missing from the document`).toBeDefined();
      expect(schema?.enum).toEqual([...values]);
    },
  );
});
