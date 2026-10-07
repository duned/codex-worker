import { useId, useRef, useState, type ReactNode, type FormEvent } from 'react';
import { Dialog, Heading, Modal, ModalOverlay } from 'react-aria-components';
import { Button } from '../untitled/components/base/buttons/button';

interface DialogProps {
  isOpen: boolean;
  title: string;
  description: string;
  onClose(): void;
  children: ReactNode;
  pending?: boolean;
}
/** React Aria owns focus containment/restoration, Escape and outside dismissal. */
export function FormDialog({ isOpen, title, description, onClose, children, pending = false }: DialogProps) {
  const descriptionId = useId();
  return <ModalOverlay isOpen={isOpen} onOpenChange={open => { if (!open && !pending) onClose(); }}
    isDismissable={!pending} isKeyboardDismissDisabled={pending}
    className="fixed inset-0 z-50 flex items-center justify-center bg-overlay/70 p-4 backdrop-blur-sm">
    <Modal className="max-h-[calc(100dvh-2rem)] w-full max-w-md overflow-y-auto rounded-xl bg-primary p-6 shadow-xl ring-1 ring-secondary">
      <Dialog aria-describedby={descriptionId} className="flex flex-col gap-4 outline-none">
        <Heading slot="title" className="text-lg font-semibold text-primary">{title}</Heading>
        <p id={descriptionId} className="text-sm text-secondary">{description}</p>
        {children}
      </Dialog>
    </Modal>
  </ModalOverlay>;
}
interface ActionDialogProps extends Omit<DialogProps, 'children' | 'pending'> {
  actionLabel: string;
  destructive?: boolean;
  disabled?: boolean;
  onSubmit(): Promise<void>;
  children?: ReactNode;
}
/** Local in-flight guard only; authoritative checks and uncertain fences belong to runtime.mutate. */
export function ActionDialog({ actionLabel, destructive = false, disabled = false, onSubmit, children, ...props }: ActionDialogProps) {
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (inFlight.current || disabled || failed) return;
    inFlight.current = true;
    setPending(true);
    try { await onSubmit(); props.onClose(); }
    catch { setFailed(true); }
    finally { inFlight.current = false; setPending(false); }
  }
  return <FormDialog {...props} pending={pending}>
    <form onSubmit={event => { void submit(event); }} className="flex flex-col gap-4">
      <fieldset disabled={pending || failed} className="contents">{children}</fieldset>
      {failed && <p role="alert" className="text-sm text-error-primary">Result unavailable. Close this dialog and review authoritative state before another action.</p>}
      {pending && <p role="status" className="text-sm text-tertiary">Submitting…</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button autoFocus color="secondary" isDisabled={pending} onPress={props.onClose}>Cancel</Button>
        <Button type="submit" color={destructive ? 'primary-destructive' : 'primary'} isDisabled={pending || disabled || failed}>{actionLabel}</Button>
      </div>
    </form>
  </FormDialog>;
}
export function ConfirmationDialog(props: Omit<ActionDialogProps, 'children'>) {
  return <ActionDialog {...props} />;
}
