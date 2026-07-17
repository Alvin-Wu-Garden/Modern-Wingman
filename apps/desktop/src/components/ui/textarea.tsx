import {
  forwardRef,
  useState,
  useId,
  type TextareaHTMLAttributes,
  type ChangeEvent,
} from 'react'
import { cn } from '@/lib/utils'

export interface TextareaProps
  extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label?: string
  error?: string
  hint?: string
  /** Show character counter below the textarea */
  showCount?: boolean
  /** Max character limit displayed in the counter (e.g. 500) */
  maxCount?: number
}

const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(
  (
    {
      label,
      error,
      hint,
      showCount = false,
      maxCount,
      className,
      id,
      value,
      defaultValue,
      disabled,
      onChange,
      ...props
    },
    ref
  ) => {
    const autoId = useId()
    const inputId = id ?? autoId

    // For uncontrolled usage, track length internally
    const [internalLength, setInternalLength] = useState(
      typeof defaultValue === 'string' ? defaultValue.length : 0
    )

    // Prefer controlled value's length; fall back to internal tracker
    const isControlled = value !== undefined
    const displayLength = isControlled
      ? typeof value === 'string'
        ? value.length
        : 0
      : internalLength

    const isOverLimit = maxCount !== undefined && displayLength > maxCount

    const handleChange = (e: ChangeEvent<HTMLTextAreaElement>) => {
      if (!isControlled) setInternalLength(e.target.value.length)
      onChange?.(e)
    }

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

        <textarea
          ref={ref}
          id={inputId}
          value={value}
          defaultValue={defaultValue}
          disabled={disabled}
          onChange={handleChange}
          className={cn(
            'w-full rounded-xl px-4 py-3 text-sm text-ink resize-none',
            'bg-input-bg border border-transparent',
            'placeholder:text-ink-subtle',
            'outline-none transition-all duration-150',
            'hover:bg-surface-alt',
            'focus:border-brand focus:ring-2 focus:ring-brand/20 focus:bg-surface',
            error && 'border-error focus:border-error focus:ring-error/20',
            disabled &&
              'bg-input-disabled text-ink-subtle cursor-not-allowed opacity-60',
            className
          )}
          {...props}
        />

        {/* Footer: hint/error + character counter */}
        {(error || hint || (showCount && maxCount !== undefined)) && (
          <div className="flex items-start justify-between gap-4">
            <div className="min-w-0 flex-1">
              {error && <p className="text-xs text-error">{error}</p>}
              {hint && !error && (
                <p className="text-xs text-ink-subtle">{hint}</p>
              )}
            </div>
            {showCount && maxCount !== undefined && (
              <p
                className={cn(
                  'text-xs tabular-nums shrink-0',
                  isOverLimit ? 'text-error font-medium' : 'text-ink-subtle'
                )}
              >
                {displayLength}/{maxCount}
              </p>
            )}
          </div>
        )}
      </div>
    )
  }
)

Textarea.displayName = 'Textarea'

export { Textarea }
