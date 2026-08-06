#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_population.h"

namespace refii::renderer {

bool IsFableTitleScenePopulationHook(std::string_view symbol) {
  return symbol == "sub_821D4F60" || // title/menu object callback dispatch
         symbol == "sub_821D9C98" || // title/menu world renderable callback
         symbol == "sub_822975F8" || // title/menu visibility traversal
         symbol == "sub_82297698" || // title/menu recursive visibility traversal
         symbol == "sub_821CB498" || // title/menu visible object enqueue
         symbol == "sub_82231ED8" || // title/menu render-list callback dispatch
         symbol == "sub_82192028" || // title/menu indirect object render callback
         symbol == "sub_821A4ED0" || // title/menu render-list insert callback
         symbol == "sub_8222C2B0" || // title/menu render-list sorted insert
         symbol == "sub_822749E0" || // title/menu renderable node producer
         symbol == "sub_82A679C0" || // title/help-page renderable node producer
         symbol == "sub_82A73DC8" || // title/menu renderable node producer variant
         symbol == "sub_82B285F8" || // title/background renderable readiness pump
         symbol == "sub_82A657C0";   // title/background renderable resource gate
}

}  // namespace refii::renderer
