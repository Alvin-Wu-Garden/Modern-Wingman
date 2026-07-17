import * as DialogPrimitive from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { type ReactNode, type HTMLAttributes } from 'react'
import { cn } from '@/lib/utils'

/* ── Primitives re-exported for advanced composition ── */
const DialogRoot = DialogPrimitive.Root
const DialogTrigger = DialogPrimitive.Trigger
const DialogClose = DialogPrimitive.Close
const DialogPortal = DialogPrimitive.Portal

/* ── Overlay ── */
function DialogOverlay({
  className,
  ...props
}: DialogPrimitive.DialogOverlayProps) {
  return (
    <DialogPrimitive.Overlay
      className={cn(
        'fixed inset-0 z-50',
        'bg-[#020416]/48',
        'transition-opacity duration-200',
        'data-[state=closed]:opacity-0 data-[state=open]:opacity-100',
        className
      )}
      {...props}
    />
  )
}

/* ── Content ── */
interface DialogContentProps extends DialogPrimitive.DialogContentProps {
  hideClose?: boolean
}

function DialogContent({
  className,
  children,
  hideClose = false,
  ...props
}: DialogContentProps) {
  return (
    <DialogPortal>
      <DialogOverlay />
      <DialogPrimitive.Content
        className={cn(
          'fixed left-1/2 top-1/2 z-50',
          '-translate-x-1/2 -translate-y-1/2',
          'w-full bg-surface rounded-2xl shadow-xl',
          'p-6',
          'transition-all duration-200',
          'data-[state=closed]:opacity-0 data-[state=closed]:scale-95',
          'data-[state=open]:opacity-100 data-[state=open]:scale-100',
          className
        )}
        {...props}
      >
        {!hideClose && (
          <DialogClose
            className={cn(
              'absolute right-4 top-4 rounded-lg p-1.5',
              'text-ink-subtle hover:text-ink hover:bg-surface-alt',
              'transition-colors duration-150',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand'
            )}
          >
            <X className="h-4 w-4" />
            <span className="sr-only">Close</span>
          </DialogClose>
        )}
        {children}
      </DialogPrimitive.Content>
    </DialogPortal>
  )
}

/* ── Sub-components ── */
function DialogHeader({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex flex-col gap-1 mb-5 pr-6', className)}
      {...props}
    />
  )
}

function DialogFooter({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'flex items-center justify-end gap-3 mt-6 pt-5 border-t border-border',
        className
      )}
      {...props}
    />
  )
}

function DialogTitle({
  className,
  ...props
}: DialogPrimitive.DialogTitleProps) {
  return (
    <DialogPrimitive.Title
      className={cn('text-lg font-semibold text-ink', className)}
      {...props}
    />
  )
}

function DialogDescription({
  className,
  ...props
}: DialogPrimitive.DialogDescriptionProps) {
  return (
    <DialogPrimitive.Description
      className={cn('text-sm text-ink-secondary', className)}
      {...props}
    />
  )
}

/* ── High-level Modal wrapper ── */
const SIZE_MAP = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
  xl: 'max-w-4xl',
} as const

export interface ModalProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title?: string
  description?: string
  children: ReactNode
  footer?: ReactNode
  size?: keyof typeof SIZE_MAP
  hideClose?: boolean
}

export function Modal({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
  size = 'md',
  hideClose,
}: ModalProps) {
  return (
    <DialogRoot open={open} onOpenChange={onOpenChange}>
      <DialogContent className={SIZE_MAP[size]} hideClose={hideClose}>
        {(title || description) && (
          <DialogHeader>
            {title && <DialogTitle>{title}</DialogTitle>}
            {description && (
              <DialogDescription>{description}</DialogDescription>
            )}
          </DialogHeader>
        )}

        {children}

        {footer && <DialogFooter>{footer}</DialogFooter>}
      </DialogContent>
    </DialogRoot>
  )
}

/* Named exports for advanced usage */
export {
  DialogRoot,
  DialogTrigger,
  DialogClose,
  DialogContent,
  DialogHeader,
  DialogFooter,
  DialogTitle,
  DialogDescription,
}
