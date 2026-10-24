import { Link } from "react-router";
import { lastUnit, usePlants } from "../../app/useCatalog";

/** First screen: pick the unit to work in. Units are grouped by plant; the number is what people say on site. */
export function UnitPickerPage() {
  const plants = usePlants();
  const last = lastUnit.get();

  return (
    <section>
      <h1 className="text-[2rem] leading-tight font-semibold">¿En qué unidad vas a trabajar?</h1>
      <p className="mt-1 text-muted">Todo lo que veas y cargues quedará dentro de esa unidad. Puedes cambiarla cuando quieras.</p>

      {plants.isPending && <p className="mt-8 text-muted">Cargando unidades…</p>}
      {plants.error && <p className="mt-8 text-hold">{plants.error.message}</p>}

      <div className="mt-8 space-y-10">
        {plants.data?.map((plant) => (
          <div key={plant.id}>
            <h2 className="text-xl font-semibold">{plant.name}</h2>
            <ul className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
              {plant.units.map((unit) => (
                <li key={unit.id}>
                  <Link
                    to={`/u/${unit.id}`}
                    onClick={() => lastUnit.set(unit.id)}
                    className="group flex h-full flex-col rounded-lg border border-line bg-panel px-4 pt-3 pb-4 transition-colors hover:border-violet focus-visible:border-violet"
                  >
                    <span className="text-sm text-muted">{unit.id === last ? "Última que usaste" : "Unit"}</span>
                    <span className="font-display text-[3.25rem] leading-none font-bold tracking-tight text-ink group-hover:text-violet">
                      {unit.number}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </section>
  );
}
