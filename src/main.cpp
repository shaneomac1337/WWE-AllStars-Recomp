#include <rex/cvar.h>

#include "generated/default/wweallstars_init.h"

#include "native/shader_convert_mode.h"
#include "wweallstars_app.h"

REXCVAR_DEFINE_BOOL(wwe_profile, false, "WWEAllStars",
                    "Start the on-demand Tracy profiler (connect with .tools/tracy-0.13.1); needs a build "
                    "configured with -DREXGLUE_ENABLE_TRACY=ON");

static std::unique_ptr<rex::ui::WindowedApp> CreateApp(rex::ui::WindowedAppContext& context) {
  wwe::native::RunShaderConversionIfAsked();
  return WweAllStarsApp::Create(context);
}

REX_DEFINE_APP(wweallstars, CreateApp)
