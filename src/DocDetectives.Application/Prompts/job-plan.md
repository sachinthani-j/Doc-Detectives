# PM model rules

A PM model is the recurring preventive maintenance of one asset in Ultimo. Each plan you return becomes one PM model:
the list of steps a technician carries out during one preventive maintenance visit at one interval.

## Does this equipment need PM models? (manuals only)
Decide first, from the manual, and fill PmModelNeeded (true/false) with a one-sentence PmModelReason.
- **Create** PM models for equipment that is part of the production process, has several maintainable components
  and needs recurring maintenance.
- **Leave out** simple, standalone or support equipment whose failure has little impact, as well as maintenance-free,
  consumable, single-check or cheap items with a backup.
- **When in doubt, leave it out.**
When PmModelNeeded is false, return no plans. Use your judgement from the manual. Refer to the system as Ultimo.
For an inspection document, leave PmModelNeeded and PmModelReason null.

## Which plans to create
- **Manual:** use only the maintenance, lubrication, inspection and cleaning schedules (e.g. "Onderhoudsschema", "Maintenance schedule"). Create one plan per interval (daily, weekly, every 500 operating hours, yearly, ...). Ignore installation, operation and troubleshooting. If the manual has no recurring maintenance, return no plans.
- **Inspection document / work instruction / checklist:** create one plan for the whole document. If it covers several machines, keep the document order and put each machine's lines together.

## Lines
- **Task** = something to do ("Lubricate all bearings with SWEPCO 116", "Replace all felt cloths").
- **Inspection** = something to check and record ("Bearings lubricated", "Belt runs centred"). Fill AllowedResults with the answers the document offers (e.g. OK / NOK, OK / BIJGEREGELD). For a measurement, fill NominalValue with the value and unit (e.g. "10-13 mm") and leave AllowedResults empty.
- Put safety steps (lockout, LOTOTO, switch off, service position) first, as Task lines.
- Title is short (a few words); Instruction holds the explanation, locations and tips.
- EquipmentCode and Component: the machine or part the line is about, exactly as printed (e.g. "PL3_DRO_100_M01", "Drogerband 1"). Leave null when the line is about the whole equipment.
- Skip empty form fields such as "Andere opmerkingen: ____" or signature boxes: remarks can be recorded on every line anyway.
- Do not invent steps, values or intervals. Keep the document's language.

## Interval
Fill Interval with the frequency as written (e.g. "4 W", "every 500 operating hours", "yearly") and IntervalValue + IntervalUnit (Hours, Days, Weeks, Months, Years) when it can be expressed that way. Leave them null when the document gives no frequency: never invent intervals.
