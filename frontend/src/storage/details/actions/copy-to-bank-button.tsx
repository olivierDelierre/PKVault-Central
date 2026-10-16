import { Tooltip } from '@mantine/core';
import { CopyIcon } from 'lucide-react';
import type React from 'react';
import { usePkmIndex } from '../../../data/hooks/use-pkm-index';
import type { PkmSaveDTO } from '../../../data/sdk/model';
import { useStorageCopyPkmToBank } from '../../../data/sdk/storage/storage.gen';
import { useTranslate } from '../../../translate/i18n';
import { UIButton } from '../../../ui/form/button/ui-button';
import { filterIsDefined } from '../../../util/filter-is-defined';
import { useSelectCallback } from '../../../util/use-select-callback';
import { BankContext } from '../../bank/bank-context';
import type { ActionButtonDefaultProps } from './edit-pkm-button';

/**
 * Copies Pokémon of a save to the selected bank: they stay in the save, and the save is not modified.
 */
export const CopyToBankButton: React.FC<ActionButtonDefaultProps> = ({ pkmIds, saveId }) => {
    const { t } = useTranslate();

    const selectedBank = BankContext.useSelectedBankBoxes().data?.selectedBank;

    const pkmIndexQuery = usePkmIndex(saveId ?? null,
        useSelectCallback(data => {
            return pkmIds.map(id => data.data.byId[ id ])
                .filter(filterIsDefined)
                .map(pkm => ({ id: pkm.id, canMoveToMain: (pkm as PkmSaveDTO).canMoveToMain }));
        }, [ pkmIds ]));

    const copyMutation = useStorageCopyPkmToBank();

    const pkms = pkmIndexQuery.data ?? [];

    const canCopy = !!saveId && !!selectedBank && pkms.length > 0 && pkms.every(pkm => pkm.canMoveToMain);

    return saveId
        ? <Tooltip
            multiline
            w={300}
            label={[ t('storage.actions.copy-to-bank.helpTitle'), t('storage.actions.copy-to-bank.helpContent') ].join('\n\n')}
        >
            <UIButton
                name='copy-to-bank'
                controlLabel={t('storage.actions.copy-to-bank')}
                onClick={() => {
                    if (!selectedBank || !saveId) {
                        return;
                    }

                    return copyMutation.mutateAsync({
                        data: {
                            pkmIds: pkms.map(pkm => pkm.id),
                            sourceSaveId: saveId,
                            bankId: selectedBank.id,
                        },
                    });
                }}
                size='compact-md'
                leftSection={<CopyIcon />}
                disabled={!canCopy}
            >
                {t('storage.actions.copy-to-bank')}
            </UIButton>
        </Tooltip>
        : null;
};
