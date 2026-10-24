import { useEffect } from "react";
import { Link, useParams } from "react-router";
import { lastUnit, useUnit } from "../../app/useCatalog";
import { PageHeader } from "../../components/PageHeader";

/** Landing page of a unit. The registers and the dashboard appear here as each phase is delivered. */
export function UnitHomePage() {
  const unitId = Number(useParams().unitId);
  const { unit, plantName, isPending } = useUnit(unitId);

  useEffect(() => {
    if (unit) lastUnit.set(unit.id);
  }, [unit]);

  if (isPending) return <p className="text-muted">Cargando unidad…</p>;
  if (!unit) {
    return (
      <div>
        <PageHeader title="Unidad no encontrada" description="La unidad no existe o fue desactivada." />
        <Link to="/unidades" className="mt-6 inline-block font-semibold text-violet">
          Elegir otra unidad
        </Link>
      </div>
    );
  }

  return (
    <div>
      <PageHeader title={unit.name} description={plantName} />
      <div className="mt-8 max-w-xl rounded-lg border border-dashed border-line bg-panel p-6">
        <h2 className="text-xl font-semibold">Todavía no hay equipos en esta unidad</h2>
        <p className="mt-2 text-muted">
          Los equipos, gates y lecturas llegan con la migración desde SharePoint. Cuando estén cargados, aquí verás el registro y el avance
          de la unidad.
        </p>
      </div>
    </div>
  );
}
