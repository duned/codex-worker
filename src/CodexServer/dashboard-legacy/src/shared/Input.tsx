import type { ValidationResult } from 'react-aria-components';
import { Input as BaseInput, type InputProps } from '../untitled/components/base/input/input';
import { t, useLanguage } from './i18n';
export { InputBase, TextField } from '../untitled/components/base/input/input';
export function validationMessage({ validationDetails }: ValidationResult): string {
  if (validationDetails.valueMissing) return t('shared.validationRequired');
  if (validationDetails.typeMismatch) return t('shared.validationType');
  if (validationDetails.rangeOverflow || validationDetails.rangeUnderflow || validationDetails.stepMismatch) return t('shared.validationRange');
  if (validationDetails.tooLong || validationDetails.tooShort) return t('shared.validationLength');
  return t('shared.validationInvalid');
}
export function Input(props: InputProps) {
  useLanguage();
  return <BaseInput {...props} passwordVisibilityLabel={t('shared.togglePasswordVisibility')} errorMessage={validationMessage} />;
}
