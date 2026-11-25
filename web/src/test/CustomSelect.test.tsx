import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { CustomSelect, type CustomSelectOption } from '../components/CustomSelect';

type SampleKey = 'Viewer' | 'Editor' | 'Owner';

const OPTIONS: readonly CustomSelectOption<SampleKey>[] = [
  { value: 'Viewer', label: 'بیننده', description: 'مشاهده اسناد و پرسش‌وپاسخ', dotColor: '#475569' },
  { value: 'Editor', label: 'ویرایشگر', description: 'بارگذاری و حذف اسناد', dotColor: '#1d4ed8' },
  { value: 'Owner', label: 'مالک', description: 'مدیریت کامل مجموعه و اعضا', dotColor: '#166534' },
];

describe('CustomSelect', () => {
  it('renders selected label, opens listbox with descriptions, and supports keyboard navigation', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();

    render(
      <div>
        <CustomSelect<SampleKey>
          ariaLabel="سطح دسترسی"
          value="Viewer"
          options={OPTIONS}
          onChange={onChange}
        />
        <button type="button">بیرون</button>
      </div>,
    );

    const trigger = screen.getByRole('combobox', { name: 'سطح دسترسی' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(trigger).toHaveTextContent('بیننده');

    trigger.focus();
    await user.keyboard('{ArrowDown}');

    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    const listbox = screen.getByRole('listbox', { name: 'سطح دسترسی' });
    expect(listbox).toBeInTheDocument();
    expect(screen.getByText('بارگذاری و حذف اسناد')).toBeInTheDocument();

    // Move to Editor and select with Enter
    await user.keyboard('{ArrowDown}{Enter}');
    expect(onChange).toHaveBeenCalledWith('Editor');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    // Open and close with Escape
    await user.click(trigger);
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();

    // Open and close by clicking outside
    await user.click(trigger);
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'بیرون' }));
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });
});
