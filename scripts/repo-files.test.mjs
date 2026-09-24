import { test } from "node:test";
import assert from "node:assert/strict";

import { parseMsBuildProperties } from "./repo-files.mjs";

test("the property reader expands a $(Name) reference to the value already read", () => {
  const props = parseMsBuildProperties(`
    <Project><PropertyGroup>
      <CoveMinVersion>1.1.0</CoveMinVersion>
      <CoveSdkVersion Condition="'$(CoveSdkVersion)' == ''">$(CoveMinVersion)</CoveSdkVersion>
    </PropertyGroup></Project>
  `);

  assert.equal(props.CoveSdkVersion, "1.1.0");
});
