# Job plan rules

A job plan is the list of steps a technician carries out during one preventive maintenance visit.

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
Fill Interval with the frequency as written (e.g. "4 W", "every 500 operating hours", "yearly") and IntervalValue + IntervalUnit (Hours, Days, Weeks, Months, Years) when it can be expressed that way. Leave them null when the document gives no frequency.
