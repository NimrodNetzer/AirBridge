package com.airbridge.app.transfer.di

import com.airbridge.app.transfer.FileTransferService
import com.airbridge.app.transfer.interfaces.IFileTransferService
import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * Hilt DI module that wires [FileTransferService] as the [IFileTransferService] singleton.
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class TransferModule {

    @Binds
    @Singleton
    abstract fun bindFileTransferService(impl: FileTransferService): IFileTransferService
}
