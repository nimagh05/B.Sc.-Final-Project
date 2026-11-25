import { useId, useRef, useState, type ChangeEvent, type DragEvent } from 'react';
import { fa } from '../i18n/fa';

interface FileDropProps {
  onFileSelected: (file: File) => void;
  disabled?: boolean;
  isUploading?: boolean;
  uploadProgress?: number;
  selectedFileName?: string | null;
  maxSizeMb?: number;
}

export function FileDrop({
  onFileSelected,
  disabled = false,
  isUploading = false,
  uploadProgress = 0,
  selectedFileName = null,
  maxSizeMb = 50,
}: FileDropProps) {
  const inputId = useId();
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [clientError, setClientError] = useState<string | null>(null);

  const validateAndEmit = (file: File | undefined) => {
    if (!file) {
      return;
    }

    if (!file.name.toLowerCase().endsWith('.pdf')) {
      setClientError(fa.collectionDetail.invalidFileType);
      return;
    }

    if (file.size <= 0) {
      setClientError(fa.collectionDetail.emptyFileError);
      return;
    }

    const maxBytes = maxSizeMb * 1024 * 1024;
    if (file.size > maxBytes) {
      setClientError(fa.collectionDetail.fileTooLargeClient(maxSizeMb));
      return;
    }

    setClientError(null);
    onFileSelected(file);
  };

  const handleInputChange = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    validateAndEmit(file);
    event.target.value = '';
  };

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    if (!disabled && !isUploading) {
      setIsDragging(true);
    }
  };

  const handleDragLeave = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setIsDragging(false);
  };

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setIsDragging(false);
    if (disabled || isUploading) {
      return;
    }
    const file = event.dataTransfer.files?.[0];
    validateAndEmit(file);
  };

  return (
    <div className="form-stack">
      <div
        className="file-drop"
        data-dragging={isDragging ? 'true' : 'false'}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <input
          ref={inputRef}
          id={inputId}
          type="file"
          accept=".pdf,application/pdf"
          className="sr-only"
          disabled={disabled || isUploading}
          onChange={handleInputChange}
        />

        <p className="file-drop-title">{fa.collectionDetail.dropTitle}</p>
        <p className="file-drop-subtitle">{fa.collectionDetail.dropSubtitle}</p>

        <button
          type="button"
          className="btn btn-primary"
          disabled={disabled || isUploading}
          onClick={() => inputRef.current?.click()}
        >
          {fa.collectionDetail.selectFileButton}
        </button>

        {isUploading && (
          <div style={{ marginBlockStart: '16px' }} aria-live="polite">
            <span className="field-hint">
              {fa.collectionDetail.uploadingFile(selectedFileName ?? 'PDF', uploadProgress)}
            </span>
            <div
              className="progress-track"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={uploadProgress}
            >
              <div className="progress-fill" style={{ width: `${uploadProgress}%` }} />
            </div>
          </div>
        )}
      </div>

      {clientError && (
        <div className="alert alert-error" role="alert">
          <p className="alert-detail">{clientError}</p>
        </div>
      )}
    </div>
  );
}
