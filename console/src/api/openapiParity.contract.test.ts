import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { PUBLIC_API_ROUTE_TEMPLATES } from './client.js';
import { enumWireShape, objectWireShape, unionWireShape } from './wireShape.js';
import type { RuntimePropertyWireShape } from './wireShape.js';

interface OpenApiSchema {
  type?: string;
  format?: string;
  nullable?: boolean;
  properties?: Record<string, unknown>;
  required?: string[];
  items?: OpenApiSchema;
  allOf?: OpenApiSchema[];
  oneOf?: OpenApiSchema[];
  $ref?: string;
  enum?: string[];
  additionalProperties?: boolean | OpenApiSchema;
  discriminator?: {
    propertyName?: string;
    mapping?: Record<string, string>;
  };
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
    '%s mirrors the complete documented schema',
    (schemaName, expectedSchema) => {
      const schema = document.components.schemas[schemaName];
      expect(schema, `${schemaName} is missing from the document`).toBeDefined();
      expect(schema?.type).toBe('object');
      expect(schema?.additionalProperties !== false).toBe(
        expectedSchema.additionalProperties,
      );

      const properties = schema?.properties ?? {};
      const expectedProperties = expectedSchema.properties as Record<
        string,
        RuntimePropertyWireShape
      >;
      expect(Object.keys(properties).sort()).toEqual(
        Object.keys(expectedProperties).sort(),
      );
      expect([...(schema?.required ?? [])].sort()).toEqual(
        Object.entries(expectedProperties)
          .filter(([, property]) => property.required)
          .map(([propertyName]) => propertyName)
          .sort(),
      );

      for (const [propertyName, expectedProperty] of Object.entries(
        expectedProperties,
      )) {
        expectPropertySchema(
          schemaName,
          propertyName,
          properties[propertyName] as OpenApiSchema | undefined,
          expectedProperty,
        );
      }
    },
  );

  it.each(Object.entries(enumWireShape))(
    '%s mirrors the documented values in order',
    (schemaName, values) => {
      const schema = document.components.schemas[schemaName];
      expect(schema, `${schemaName} is missing from the document`).toBeDefined();
      expect(schema?.type).toBe('string');
      expect(schema?.enum).toEqual([...values]);
    },
  );

  it.each(Object.entries(unionWireShape))(
    '%s mirrors the documented discriminated union',
    (schemaName, expectedSchema) => {
      const schema = document.components.schemas[schemaName];
      expect(schema, `${schemaName} is missing from the document`).toBeDefined();
      expect(schema?.discriminator?.propertyName).toBe(expectedSchema.discriminator);
      expect(schema?.oneOf?.map(referenceName).sort()).toEqual(
        [...expectedSchema.schemas].sort(),
      );
      expect(Object.keys(schema?.discriminator?.mapping ?? {}).sort()).toEqual(
        [...expectedSchema.schemas]
          .map((name) => name.replace(/^Inbox|MessageContent$/g, ''))
          .sort(),
      );
    },
  );
});

function expectPropertySchema(
  schemaName: string,
  propertyName: string,
  actual: OpenApiSchema | undefined,
  expected: RuntimePropertyWireShape,
): void {
  const location = `${schemaName}.${propertyName}`;
  expect(actual, `${location} is missing from the document`).toBeDefined();
  expect(actual?.nullable ?? false, `${location} nullability changed`).toBe(
    expected.nullable,
  );

  if (expected.type === 'reference') {
    expect(referenceName(actual), `${location} reference changed`).toBe(expected.schema);
    return;
  }

  expect(actual?.type, `${location} type changed`).toBe(expected.type);
  if (expected.type === 'array') {
    expectArrayItemSchema(location, actual?.items, expected.items);
    return;
  }

  // Booleans carry no format, so the mirror declares none and the document must
  // agree rather than the check quietly skipping the comparison.
  expect(actual?.format, `${location} format changed`).toBe(
    'format' in expected ? expected.format : undefined,
  );
}

function expectArrayItemSchema(
  location: string,
  actual: OpenApiSchema | undefined,
  expected: { type: 'string' } | { type: 'reference'; schema: string },
): void {
  expect(actual, `${location} item schema is missing`).toBeDefined();
  if (expected.type === 'reference') {
    expect(referenceName(actual), `${location} item reference changed`).toBe(
      expected.schema,
    );
    return;
  }

  expect(actual?.type, `${location} item type changed`).toBe(expected.type);
}

function referenceName(schema: OpenApiSchema | undefined): string | undefined {
  const reference = schema?.$ref ??
    (schema?.allOf?.length === 1 ? schema.allOf[0]?.$ref : undefined);
  return reference?.split('/').at(-1);
}
