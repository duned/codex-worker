import { useLanguage } from './i18n';
import { FieldError } from 'react-aria-components';
import { validationMessage } from './Input';
import { InputBase, TextField } from './Input';
import { Label } from '../untitled/components/base/input/label';
/** Bounded integer composition using the retained public input source. */
export function NumberInput({ label, value, onChange, min, max, isRequired = false }: {
  label: string; value: string; onChange(value: string): void; min: number; max: number; isRequired?: boolean;
}) {
  useLanguage();
  return <TextField value={value} onChange={onChange} isRequired={isRequired} type="number" validationBehavior="native">
    <Label isRequired={isRequired}>{label}</Label><InputBase type="number" min={min} max={max} step={1} isRequired={isRequired} />
  <FieldError className="text-sm text-error-primary">{validationMessage}</FieldError></TextField>;
}
