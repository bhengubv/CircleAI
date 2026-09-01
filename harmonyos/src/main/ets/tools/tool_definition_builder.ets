/**
 * tools/tool_definition_builder
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools/tool_definition_builder.ts
//
// Fluent builder for constructing ToolDefinition instances. Port of
// CircleAI.Tools.ToolDefinitionBuilder. Accumulates parameters and builds an
// immutable ToolDefinition on build().

import type { ToolDefinition, ToolParameter } from "./index";

interface PendingParam {
  readonly name: string;
  readonly parameter: ToolParameter;
  readonly required: boolean;
}

/**
 * Fluent builder for {@link ToolDefinition}. Mirrors
 * `CircleAI.Tools.ToolDefinitionBuilder`.
 *
 * @example
 * const tool = ToolDefinitionBuilder.create("get_weather")
 *   .description("Get current weather for a location")
 *   .parameter("city", "string", "The city name", true)
 *   .parameter("units", "string", "Temperature units", false, ["celsius", "fahrenheit"])
 *   .build();
 */
export class ToolDefinitionBuilder {
  private readonly nameValue: string;
  private descriptionValue?: string;
  private readonly parameters: PendingParam[] = [];

  private constructor(name: string) {
    this.nameValue = name;
  }

  /**
   * Creates a new builder for a tool with the given `name`. Typically a
   * snake_case identifier (e.g. `"get_weather"`).
   */
  static create(name: string): ToolDefinitionBuilder {
    if (name === null || name === undefined || name.length === 0) {
      throw new Error("name must be non-null and non-empty.");
    }
    return new ToolDefinitionBuilder(name);
  }

  /** Sets the human-readable description for the tool. */
  description(description: string): this {
    if (description === null || description === undefined || description.length === 0) {
      throw new Error("description must be non-null and non-empty.");
    }
    this.descriptionValue = description;
    return this;
  }

  /**
   * Adds a parameter to the tool definition.
   *
   * @param type JSON Schema type: "string" | "number" | "boolean" | "object" | "array".
   * @param required When true, the parameter is added to the required list.
   * @param enumValues Optional set of allowed values (for string-typed parameters).
   */
  parameter(
    name: string,
    type: string,
    description: string,
    required = false,
    enumValues?: string[],
  ): this {
    if (name === null || name === undefined || name.length === 0) {
      throw new Error("name must be non-null and non-empty.");
    }
    if (type === null || type === undefined || type.length === 0) {
      throw new Error("type must be non-null and non-empty.");
    }
    if (description === null || description === undefined || description.length === 0) {
      throw new Error("description must be non-null and non-empty.");
    }

    const param: ToolParameter = { type, description, enum: enumValues };
    this.parameters.push({ name, parameter: param, required });
    return this;
  }

  /**
   * Builds the final {@link ToolDefinition} from the accumulated state.
   *
   * @throws Error when {@link description} was not called before {@link build}.
   */
  build(): ToolDefinition {
    if (this.descriptionValue === undefined || this.descriptionValue.length === 0) {
      throw new Error(
        `ToolDefinition '${this.nameValue}' requires a description. Call description() before build().`,
      );
    }

    const parameters: Record<string, ToolParameter> = {};
    const required: string[] = [];
    for (const { name, parameter, required: isRequired } of this.parameters) {
      parameters[name] = parameter;
      if (isRequired) required.push(name);
    }

    return {
      name: this.nameValue,
      description: this.descriptionValue,
      parameters,
      requiredParameters: required,
    };
  }
}
