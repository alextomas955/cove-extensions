// Records which rate-limiting assemblies the shipped Cove image already carries, and which
// framework versions it ships them under.
//
// The question matters because an assembly the host provides must never travel inside an extension:
// a bundled copy loads into the extension's own context and gives every type crossing the boundary a
// second identity, which breaks casts and dependency injection with no error naming the cause.
//
// It is asked of the RUNNING harness container through the harness's own handle, so everything this
// row touches stays Testcontainers-managed and is reaped with the rest of the probe.
const SHARED_ROOT = "/usr/share/dotnet/shared";

const ASSEMBLIES = [
  "System.Threading.RateLimiting.dll",
  "Microsoft.AspNetCore.RateLimiting.dll",
  "Microsoft.Extensions.Http.dll",
];

const lines = (output) =>
  output
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

export const row = {
  id: "row-10-host-ratelimiting",
  label:
    "Which rate-limiting assemblies the shipped Cove image provides, and under which framework",
  requires: {
    cove: true,
    whisparr: [],
    seedHistory: false,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    const listed = await ctx.harness.exec([
      "sh",
      "-c",
      `ls -d ${SHARED_ROOT}/*/*/ 2>/dev/null || true`,
    ]);
    const frameworks = lines(listed.output).map((path) =>
      path.replace(`${SHARED_ROOT}/`, "").replace(/\/$/, ""),
    );

    // The assembly names are this file's own literals, so nothing the container reported is composed
    // back into a command it then runs.
    const probed = await ctx.harness.exec([
      "sh",
      "-c",
      `for assembly in ${ASSEMBLIES.join(" ")}; do for dir in ${SHARED_ROOT}/*/*/; do ` +
        `if [ -f "$dir$assembly" ]; then echo "$assembly $dir"; fi; done; done; exit 0`,
    ]);

    const assemblies = Object.fromEntries(
      ASSEMBLIES.map((name) => [name, { present: false, in: [] }]),
    );
    for (const line of lines(probed.output)) {
      const [name, dir] = line.split(" ");
      const entry = assemblies[name];
      if (entry === undefined) continue;
      entry.present = true;
      entry.in.push(dir.replace(`${SHARED_ROOT}/`, "").replace(/\/$/, ""));
    }

    const missing = ASSEMBLIES.filter((name) => !assemblies[name].present);

    return {
      method: {
        verb: "exec",
        path: SHARED_ROOT,
        inputs: {
          assemblies: ASSEMBLIES.length,
          via: "the harness handle's exec into the running Cove container",
        },
      },
      verdict: missing.length === 0 ? "host-provided" : "incomplete",
      observed: {
        coveImage: ctx.builds.cove,
        sharedFrameworkRoot: SHARED_ROOT,
        frameworks,
        assemblies,
        missing,
        conclusion:
          "These are provided by the host and must be referenced with Private=false, never shipped inside an extension package.",
      },
    };
  },
};
