# Characteristic rules

Characteristics are the fixed properties of one asset, as stored on the asset in the EAM (e.g. Head 32 m, rpm 1450, Width 600 mm).

## What to include
- Physical and identifying properties: dimensions, weight, capacity, power, voltage, current, frequency, speed (rpm), pressure, flow, temperature range, IP rating, refrigerant, fuel, connection sizes and similar.
- Not procedures, intervals, tolerances for adjustment, safety warnings or spare parts.
- Only values that apply to this asset's model, or to every model in the manual. When the manual lists several models and you cannot tell which value belongs to this asset, leave that characteristic out.

## Using the master table
- If a characteristic in the master table means the same thing (in any language or wording, e.g. "Opvoerhoogte" or "Delivery head" = "Head"), use its exact Name, ValueType and Unit, and convert the value to that unit (e.g. 1.2 m -> 1200 when the unit is Millimetre).
- Only create a new characteristic when nothing in the master table matches. Give it a short, generic English name in title case (e.g. "Motor power", "Max. operating pressure"), the most fitting ValueType, the full English unit name (e.g. "Kilowatt") and the unit symbol (e.g. "kW").

## Value types
- Numeric: one number in NumericValue (no ranges; a range like "10-13" becomes Alphanumeric).
- YesNo: true/false in BooleanValue.
- Alphanumeric: text in TextValue (e.g. "IP55", "R410A", "230 V / 50 Hz").
- Date: ISO date (yyyy-MM-dd) in DateValue.
- MultipleChoice: the chosen option in TextValue.

Always fill SourceParameter with the parameter name exactly as the manual wrote it and SourceText with the original "parameter: value" text. Do not invent values.
