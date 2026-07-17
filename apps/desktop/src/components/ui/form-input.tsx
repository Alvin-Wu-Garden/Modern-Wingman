import { type InputHTMLAttributes, forwardRef, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface FormInputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string
  error?: string
  hint?: string
  leadingIcon?: ReactNode
  trailingIcon?: ReactNode
}

const FormInput = forwardRef<HTMLInputElement, FormInputProps>(
  (
    { label, error, hint, leadingIcon, trailingIcon, className, disabled, id, ...props },
    ref
  ) => {
    const inputId = id ?? label?.toLowerCase().replace(/\s+/g, '-')

    return (
      <div className="flex flex-col gap-1.5 w-full">
        {label && (
          <label
            htmlFor={inputId}
            className="text-sm font-medium text-ink select-none"
          >
            {label}
          </label>
        )}

        <div className="relative flex items-center">
          {leadingIcon && (
            <span className="absolute left-3 flex items-center text-ink-subtle pointer-events-none">
              {leadingIcon}
            </span>
          )}

          <input
            ref={ref}
            id={inputId}
            disabled={disabled}
            className={cn(
              'w-full rounded-xl px-4 py-2.5 text-sm text-ink',
              'bg-input-bg border border-transparent',
              'placeholder:text-ink-subtle',
              'outline-none transition-all duration-150',
              'hover:bg-surface-alt',
              'focus:border-brand focus:ring-2 focus:ring-brand/20 focus:bg-surface',
              error && 'border-error focus:border-error focus:ring-error/20',
              disabled && 'bg-input-disabled text-ink-subtle cursor-not-allowed opacity-60',
              leadingIcon && 'pl-10',
              trailingIcon && 'pr-10',
              className
            )}
            {...props}
          />

          {trailingIcon && (
            <span className="absolute right-3 flex items-center text-ink-subtle pointer-events-none">
              {trailingIcon}
            </span>
          )}
        </div>

        {error && (
          <p className="text-xs text-error flex items-center gap-1">{error}</p>
        )}

        {hint && !error && (
          <p className="text-xs text-ink-subtle">{hint}</p>
        )}
      </div>
    )
  }
)

FormInput.displayName = 'FormInput'

export { FormInput }
