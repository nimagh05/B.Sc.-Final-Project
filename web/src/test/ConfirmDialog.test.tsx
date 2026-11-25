import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmDialog } from '../components/ConfirmDialog';

describe('ConfirmDialog', () => {
  it('renders dialog copy, triggers confirm/cancel, and closes on Escape', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const onCancel = vi.fn();

    render(
      <ConfirmDialog
        open
        title="حذف سند"
        description="آیا از حذف این سند مطمئن هستید؟"
        confirmLabel="تأیید حذف"
        cancelLabel="انصراف"
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    );

    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByText('آیا از حذف این سند مطمئن هستید؟')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'تأیید حذف' }));
    expect(onConfirm).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole('button', { name: 'انصراف' }));
    expect(onCancel).toHaveBeenCalledTimes(1);

    await user.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(2);
  });

  it('disables buttons and ignores Escape while busy', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();

    render(
      <ConfirmDialog
        open
        title="حذف مجموعه"
        description="متن تأیید"
        isBusy
        onConfirm={vi.fn()}
        onCancel={onCancel}
      />,
    );

    const buttons = screen.getAllByRole('button');
    for (const btn of buttons) {
      expect(btn).toBeDisabled();
    }

    await user.keyboard('{Escape}');
    expect(onCancel).not.toHaveBeenCalled();
  });
});
