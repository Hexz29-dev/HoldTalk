import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';

const selected = {
  Home: 'home/HomeLinear', History: 'note-text/NoteTextLinear', Settings: 'setting-2/Setting2Linear',
  Microphone: 'microphone/MicrophoneLinear', Copy: 'copy/CopyLinear', Minimize: 'minus/MinusLinear',
  Maximize: 'maximize-4/Maximize4Linear', Close: 'close-square/CloseSquareLinear',
  Recording: 'record/RecordLinear', Transcribing: 'clock-circle/ClockCircleLinear', Dropdown: 'alt-arrow-down/AltArrowDownLinear',
};
const escapeXml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');

// Export only monochrome line icons; reject unsupported SVG features instead of dropping them.
function paths(element) {
  if (Array.isArray(element)) return element.flatMap(paths);
  assert(element && typeof element === 'object', 'Expected an SVG element');
  if (typeof element.type === 'function') return paths(element.type(element.props));
  const { children, ...props } = element.props;
  if (element.type === 'svg') {
    assert.equal(props.viewBox, '0 0 24 24');
    assert.equal(props.fill, 'none');
    return paths(children);
  }
  for (const attribute of Object.keys(props)) {
    assert(['d', 'cx', 'cy', 'r', 'stroke', 'strokeWidth', 'strokeLinecap', 'strokeLinejoin', 'strokeMiterlimit'].includes(attribute), `Unsupported ${attribute}`);
  }
  assert.equal(props.stroke, 'currentColor');
  assert.equal(Number(props.strokeWidth), 1.5);
  if (element.type === 'path') return [props.d];
  assert.equal(element.type, 'circle');
  const x = Number(props.cx), y = Number(props.cy), r = Number(props.r);
  assert([x, y, r].every(Number.isFinite) && r > 0);
  return [`M${x-r},${y} A${r},${r} 0 1 0 ${x+r},${y} A${r},${r} 0 1 0 ${x-r},${y} Z`];
}

const geometries = await Promise.all(Object.entries(selected).map(async ([name, component]) => {
  const { default: icon } = await import(new URL(`node_modules/mx-icons/dist/components/${component}.js`, import.meta.url));
  assert.equal(typeof icon, 'function', `Missing ${component}`);
  const data = paths(icon({}));
  assert(data.length > 0 && data.every(path => typeof path === 'string' && path.startsWith('M')));
  return `    <!-- ${component} -->\n    <Geometry x:Key="Mx${name}">${escapeXml(data.join(' '))}</Geometry>`;
}));
const header = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">';
writeFileSync(new URL('../../src/Assets/MxIcons.xaml', import.meta.url), `${header}\n    <!-- Generated from mx-icons 1.1.2 (MIT). Run npm run export in design/icons. -->\n${geometries.join('\n')}\n</ResourceDictionary>\n`);
writeFileSync(new URL('../../third-party/mx-icons-LICENSE.txt', import.meta.url), readFileSync(new URL('node_modules/mx-icons/LICENSE', import.meta.url)));
console.log(`Exported ${geometries.length} native vector icons.`);
