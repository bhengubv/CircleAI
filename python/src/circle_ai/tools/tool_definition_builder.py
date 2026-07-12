# tool_definition_builder.py
#
# Port of CircleAI.Tools ToolDefinitionBuilder.cs (C# — the EXACT spec).
#
# Fluent builder for constructing ToolDefinition instances. Accumulates
# parameters in a list and builds an immutable dict on build().

from __future__ import annotations

from typing import List, Optional, Tuple

from .tool_types import ToolDefinition, ToolParameter


class ToolDefinitionBuilder:
    """Fluent builder for constructing :class:`ToolDefinition` instances.

    Mirrors ``CircleAI.Tools.ToolDefinitionBuilder``.

    Example::

        tool = (
            ToolDefinitionBuilder.create("get_weather")
            .description("Get current weather for a location")
            .parameter("city", "string", "The city name", required=True)
            .parameter("units", "string", "Temperature units", required=False,
                       enum_values=["celsius", "fahrenheit"])
            .build()
        )
    """

    def __init__(self, name: str) -> None:
        self._name = name
        self._description: Optional[str] = None
        self._parameters: List[Tuple[str, ToolParameter, bool]] = []

    @staticmethod
    def create(name: str) -> "ToolDefinitionBuilder":
        """Create a new builder for a tool with the given ``name``.

        :param name: The tool name. Must be non-null and non-empty. Typically a
            snake_case identifier matching the function-call schema
            (e.g. ``"get_weather"``).
        :raises ValueError: when ``name`` is ``None`` or empty.
        """
        if name is None or name == "":
            raise ValueError("name must not be null or empty")
        return ToolDefinitionBuilder(name)

    def description(self, description: str) -> "ToolDefinitionBuilder":
        """Set the human-readable description for the tool.

        :raises ValueError: when ``description`` is ``None`` or empty.
        """
        if description is None or description == "":
            raise ValueError("description must not be null or empty")
        self._description = description
        return self

    def parameter(
        self,
        name: str,
        type: str,
        description: str,
        required: bool = False,
        enum_values: Optional[List[str]] = None,
    ) -> "ToolDefinitionBuilder":
        """Add a parameter to the tool definition.

        :param name: The parameter name. Must be non-null and non-empty.
        :param type: The JSON Schema type: ``"string"``, ``"number"``,
            ``"boolean"``, ``"object"``, or ``"array"``.
        :param description: A human-readable description of the parameter.
        :param required: When ``True``, the parameter is added to the required
            list. Default ``False``.
        :param enum_values: Optional set of allowed values (for string-typed
            parameters). Default ``None``.
        :raises ValueError: when ``name``, ``type``, or ``description`` is
            ``None`` or empty.
        """
        if name is None or name == "":
            raise ValueError("name must not be null or empty")
        if type is None or type == "":
            raise ValueError("type must not be null or empty")
        if description is None or description == "":
            raise ValueError("description must not be null or empty")

        param = ToolParameter(type=type, description=description, enum=enum_values)
        self._parameters.append((name, param, required))
        return self

    def build(self) -> ToolDefinition:
        """Build the final :class:`ToolDefinition` from the accumulated state.

        :raises RuntimeError: when :meth:`description` was not called before
            :meth:`build` (mirrors the C# ``InvalidOperationException``).
        """
        if self._description is None or self._description == "":
            raise RuntimeError(
                f"ToolDefinition '{self._name}' requires a description. "
                f"Call description() before build()."
            )

        parameters = {}
        required: List[str] = []
        for name, param, is_required in self._parameters:
            parameters[name] = param
            if is_required:
                required.append(name)

        return ToolDefinition(
            name=self._name,
            description=self._description,
            parameters=parameters,
            required_parameters=required,
        )
