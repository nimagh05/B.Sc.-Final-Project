import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { CheckIcon, ChevronDownIcon } from './Icons';

export interface CustomSelectOption<T extends string> {
  value: T;
  label: string;
  description?: string;
  dotColor?: string;
}

interface CustomSelectProps<T extends string> {
  id?: string;
  value: T;
  options: readonly CustomSelectOption<T>[];
  onChange: (value: T) => void;
  disabled?: boolean;
  compact?: boolean;
  alignEnd?: boolean;
  ariaLabel?: string;
  ariaLabelledBy?: string;
  className?: string;
}

export function CustomSelect<T extends string>({
  id,
  value,
  options,
  onChange,
  disabled = false,
  compact = false,
  alignEnd = false,
  ariaLabel,
  ariaLabelledBy,
  className = '',
}: CustomSelectProps<T>) {
  const [isOpen, setIsOpen] = useState(false);
  const [placement, setPlacement] = useState<'bottom' | 'top'>('bottom');
  const [highlightedIndex, setHighlightedIndex] = useState<number>(() =>
    Math.max(
      0,
      options.findIndex((o) => o.value === value),
    ),
  );

  const rootRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const listboxId = useId();
  const generatedId = useId();
  const triggerId = id ?? generatedId;

  const selectedOption = options.find((o) => o.value === value) ?? options[0];
  const selectedIndex = Math.max(
    0,
    options.findIndex((o) => o.value === value),
  );

  useEffect(() => {
    if (!isOpen) {
      setHighlightedIndex(selectedIndex);
    }
  }, [isOpen, selectedIndex]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const handlePointerDown = (event: MouseEvent | TouchEvent) => {
      const target = event.target as Node | null;
      if (rootRef.current && target && !rootRef.current.contains(target)) {
        setIsOpen(false);
      }
    };

    const handleWindowKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsOpen(false);
        triggerRef.current?.focus();
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('touchstart', handlePointerDown);
    window.addEventListener('keydown', handleWindowKeyDown);

    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('touchstart', handlePointerDown);
      window.removeEventListener('keydown', handleWindowKeyDown);
    };
  }, [isOpen]);

  const computePlacement = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect || (rect.bottom === 0 && rect.top === 0)) {
      setPlacement('bottom');
      return;
    }
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    if (spaceBelow < 230 && spaceAbove > spaceBelow) {
      setPlacement('top');
    } else {
      setPlacement('bottom');
    }
  };

  const openMenu = () => {
    if (disabled || options.length === 0) return;
    computePlacement();
    setHighlightedIndex(selectedIndex);
    setIsOpen(true);
  };

  const selectByIndex = (index: number) => {
    const option = options[index];
    if (!option) return;
    onChange(option.value);
    setIsOpen(false);
    triggerRef.current?.focus();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled || options.length === 0) return;

    switch (event.key) {
      case 'ArrowDown': {
        event.preventDefault();
        if (!isOpen) {
          openMenu();
        } else {
          setHighlightedIndex((prev) => (prev + 1) % options.length);
        }
        break;
      }
      case 'ArrowUp': {
        event.preventDefault();
        if (!isOpen) {
          openMenu();
        } else {
          setHighlightedIndex((prev) => (prev - 1 + options.length) % options.length);
        }
        break;
      }
      case 'Home': {
        if (isOpen) {
          event.preventDefault();
          setHighlightedIndex(0);
        }
        break;
      }
      case 'End': {
        if (isOpen) {
          event.preventDefault();
          setHighlightedIndex(options.length - 1);
        }
        break;
      }
      case 'Enter':
      case ' ': {
        event.preventDefault();
        if (!isOpen) {
          openMenu();
        } else {
          selectByIndex(highlightedIndex);
        }
        break;
      }
      case 'Escape': {
        if (isOpen) {
          event.preventDefault();
          event.stopPropagation();
          setIsOpen(false);
        }
        break;
      }
      case 'Tab': {
        if (isOpen) {
          setIsOpen(false);
        }
        break;
      }
      default:
        break;
    }
  };

  const activeDescendantId = isOpen ? `${listboxId}-opt-${highlightedIndex}` : undefined;

  return (
    <div
      ref={rootRef}
      className={`custom-select ${compact ? 'custom-select-compact' : ''} ${alignEnd ? 'custom-select-align-end' : ''} ${className}`.trim()}
      data-open={isOpen ? 'true' : 'false'}
      data-placement={placement}
    >
      <button
        ref={triggerRef}
        id={triggerId}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={isOpen}
        aria-controls={listboxId}
        aria-activedescendant={activeDescendantId}
        aria-label={ariaLabel}
        aria-labelledby={ariaLabelledBy}
        disabled={disabled}
        className={`custom-select-trigger ${compact ? 'custom-select-trigger-compact' : ''}`}
        onClick={() => (isOpen ? setIsOpen(false) : openMenu())}
        onKeyDown={handleKeyDown}
      >
        <span className="custom-select-trigger-value">
          {selectedOption?.dotColor && (
            <span
              className="custom-select-dot"
              style={{ backgroundColor: selectedOption.dotColor }}
              aria-hidden="true"
            />
          )}
          <span className="custom-select-trigger-label">{selectedOption?.label ?? ''}</span>
        </span>
        <ChevronDownIcon className="ui-icon custom-select-chevron" />
      </button>

      {isOpen && (
        <ul
          id={listboxId}
          role="listbox"
          aria-label={ariaLabel}
          aria-labelledby={ariaLabelledBy}
          className="custom-select-menu panel-reveal"
          tabIndex={-1}
        >
          {options.map((option, idx) => {
            const isSelected = option.value === value;
            const isHighlighted = idx === highlightedIndex;
            return (
              <li
                key={option.value}
                id={`${listboxId}-opt-${idx}`}
                role="option"
                aria-selected={isSelected}
                data-selected={isSelected ? 'true' : 'false'}
                data-highlighted={isHighlighted ? 'true' : 'false'}
                className="custom-select-option"
                onMouseEnter={() => setHighlightedIndex(idx)}
                onMouseDown={(e) => {
                  e.preventDefault();
                }}
                onClick={() => selectByIndex(idx)}
              >
                <span className="custom-select-option-main">
                  <span className="custom-select-option-title-row">
                    {option.dotColor && (
                      <span
                        className="custom-select-dot"
                        style={{ backgroundColor: option.dotColor }}
                        aria-hidden="true"
                      />
                    )}
                    <span className="custom-select-option-label">{option.label}</span>
                  </span>
                  {option.description && (
                    <span className="custom-select-option-desc">{option.description}</span>
                  )}
                </span>
                {isSelected && <CheckIcon className="ui-icon custom-select-check" />}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
